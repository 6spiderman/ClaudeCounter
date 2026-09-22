using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Settings;
using ClaudeCounter.UI;

namespace ClaudeCounter;

/// <summary>
/// Linux counterpart of the Windows build's TrayApplicationContext: same
/// wiring (settings, OAuth endpoint, polling service, tray icon, dialogs),
/// against an Avalonia TrayIcon instead of NotifyIcon. Kept close in shape and
/// naming deliberately, so a future UI consolidation is close to mechanical.
/// </summary>
public sealed class TrayApplicationContext
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly OAuthTokenEndpoint _tokenEndpoint = new();
    private readonly OAuthTokenRefresher _refresher;
    private readonly OAuthCodeExchanger _exchanger;
    private readonly ISessionStore _sessionStore =
        new SecretServiceSessionStore(new LinuxSessionStore(new MachineKeyDataProtector()));
    private readonly UsageClient _usageClient = new();
    private readonly UpdateChecker _updates = new();
    private readonly PollingService _polling;
    private readonly SleepResumeWatcher _sleepResume = new();
    private readonly FlyoutWindow _flyout;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _signInItem;
    private readonly NativeMenuItem _updateItem;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Set the moment Exit is chosen, so a stray cancellation from
    /// Avalonia's own teardown on the way out reads as "exiting", not a crash.</summary>
    public static bool IsExiting { get; private set; }

    private SettingsWindow? _settingsWindow;
    private SignInWindow? _signInWindow;
    private WindowIcon? _currentIcon;
    private (string Text, Band Band)? _iconKey;
    private bool _updateCheckStarted;
    private string? _updateUrl;

    public TrayApplicationContext(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        bool isFirstRun;
        (_settings, isFirstRun) = _settingsStore.Load();
        if (isFirstRun)
        {
            _settingsStore.Save(_settings);
            if (_settings.AutostartEnabled)
                AutostartManager.Enable();
        }
        else if (_settings.AutostartEnabled)
        {
            AutostartManager.EnsurePathCurrent();
        }

        _refresher = new OAuthTokenRefresher(_tokenEndpoint);
        _exchanger = new OAuthCodeExchanger(_tokenEndpoint);

        _polling = new PollingService(
            new TokenProvider(_sessionStore, _refresher),
            _usageClient, () => _settings.PollIntervalMinutes);

        _flyout = new FlyoutWindow(() => _settings);
        _flyout.RefreshRequested += () => _polling.TriggerNow();
        _flyout.UpdateRequested += OpenUpdatePage;
        _flyout.SignInRequested += ShowSignIn;

        _signInItem = new NativeMenuItem("Sign in to Claude...");
        _signInItem.Click += (_, _) => ShowSignIn();

        _updateItem = new NativeMenuItem("Update available") { IsVisible = false };
        _updateItem.Click += (_, _) => OpenUpdatePage();

        var refreshItem = new NativeMenuItem("Refresh now");
        refreshItem.Click += (_, _) => _polling.TriggerNow();

        var settingsItem = new NativeMenuItem("Settings...");
        settingsItem.Click += (_, _) => ShowSettings();

        var logItem = new NativeMenuItem("Open log folder");
        logItem.Click += (_, _) => Shell.ShowLogFolder();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu
        {
            refreshItem,
            _signInItem,
            settingsItem,
            new NativeMenuItemSeparator(),
            _updateItem,
            logItem,
            new NativeMenuItemSeparator(),
            exitItem,
        };

        _trayIcon = new TrayIcon
        {
            ToolTipText = "ClaudeCounter - waiting for first update",
            Menu = menu,
            IsVisible = true,
        };
        SetIcon("--", Band.Gray);
        _trayIcon.Clicked += (_, _) => ToggleFlyout();

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);

        _polling.Updated += OnPollUpdated;
        _polling.Start();

        // The poll loop's Task.Delay timer is frozen while the machine
        // sleeps, so on wake the tray shows stale data until the (delayed)
        // timer fires. Force an immediate refresh on resume instead.
        _sleepResume.Resumed += OnResumed;

        Log.Info($"ClaudeCounter {AppInfo.DisplayVersion} started (Linux).");
        TrayPresence.WarnIfMissing();

        MaybeShowOnboarding();
    }

    private void OnResumed()
    {
        Log.Info("System resumed from sleep - refreshing now.");
        _polling.TriggerNow();
    }

    /// <summary>
    /// First run only, and only when there is actually nothing set up. A
    /// reinstall over a live session, or a machine driven by the environment
    /// variable, must not be walked through a wizard it does not need.
    /// </summary>
    private void MaybeShowOnboarding()
    {
        if (_settings.OnboardingCompleted)
            return;
        if (_sessionStore.Read() is not null ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenProvider.EnvVarName)))
        {
            _settings.OnboardingCompleted = true;
            SaveSettings();
            return;
        }

        Dispatcher.UIThread.Post(ShowOnboarding);
    }

    private void ShowOnboarding()
    {
        var coordinator = new SignInCoordinator(_exchanger, _sessionStore);
        var wizard = new OnboardingWindow(coordinator, _settings);

        // Poll as soon as they sign in rather than waiting for the wizard to
        // close.
        wizard.SignInCompleted += () => _polling.TriggerNow();
        wizard.Closed += (_, _) =>
        {
            SaveSettings();
            if (_settings.AutostartEnabled)
                AutostartManager.Enable();
            else
                AutostartManager.Disable();
            Log.Info($"Onboarding finished. Signed in: {wizard.SignedIn}.");
            _polling.TriggerNow();
        };
        wizard.Show();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates || AppInfo.IsDevBuild)
            return;
        if (_settings.LastUpdateCheckUtc is { } last &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        SaveSettings();

        UpdateCheck result;
        try
        {
            result = await _updates.CheckAsync(AppInfo.Version, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result is not UpdateCheck.Available available)
        {
            if (result is UpdateCheck.Failed failed)
                Log.Warn($"Update check failed: {failed.Message}");
            return;
        }

        if (string.Equals(available.Version, _settings.SkippedVersion, StringComparison.Ordinal))
            return;

        Log.Info($"Update available: v{available.Version}");
        _updateUrl = available.HtmlUrl;
        _updateItem.Header = $"Update available: v{available.Version}";
        _updateItem.IsVisible = true;
        _flyout.ShowUpdateAvailable(available.Version);
    }

    private void OpenUpdatePage() => Shell.OpenUrl(_updateUrl ?? AppInfo.ReleasesUrl);

    private void ToggleFlyout()
    {
        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }
        _flyout.ShowNearTray();
    }

    private void OnPollUpdated(PollState state)
    {
        UpdateIcon(state);
        _trayIcon.ToolTipText = BuildTooltip(state);
        _flyout.UpdateState(state);
        UpdateSignInItem(state);

        if (!_updateCheckStarted && state.Problem == ProblemKind.None)
        {
            _updateCheckStarted = true;
            _ = CheckForUpdatesAsync();
        }
    }

    private void UpdateIcon(PollState state)
    {
        string text;
        Band band;
        if (state.Snapshot?.FiveHour is { } fiveHour)
        {
            text = $"{Math.Round(fiveHour.Utilization):0}";
            band = state.Problem == ProblemKind.None
                ? IconRenderer.BandFor(fiveHour.Utilization, _settings)
                : Band.Gray;
        }
        else
        {
            text = "--";
            band = Band.Gray;
        }
        SetIcon(text, band);
    }

    private void SetIcon(string text, Band band)
    {
        if (_iconKey == (text, band))
            return;
        _currentIcon = IconRenderer.Render(text, band);
        _trayIcon.Icon = _currentIcon;
        _iconKey = (text, band);
    }

    private static string BuildTooltip(PollState state)
    {
        if (state.Snapshot is { } s)
        {
            var now = DateTimeOffset.Now;
            var parts = new List<string> { "Claude Max" };
            if (s.FiveHour is { } fh)
                parts.Add($"Session: {fh.Utilization:0}%{ResetSuffix(fh.ResetsAt, now)}");
            if (s.SevenDay is { } sd)
                parts.Add($"Week: {sd.Utilization:0}%{ResetSuffix(sd.ResetsAt, now)}");
            if (state.Problem != ProblemKind.None && state.ProblemMessage is { } problem)
                parts.Add(problem);
            else if (state.LastSuccessAt is { } last && DateTimeOffset.Now - last > TimeSpan.FromMinutes(10))
                parts.Add($"Data {(int)(DateTimeOffset.Now - last).TotalMinutes} min old");
            return string.Join(" | ", parts);
        }
        return "ClaudeCounter - " + (state.ProblemMessage ?? "Waiting for first update");
    }

    private static string ResetSuffix(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } at ? $", resets in {TimeText.Countdown(at, now)}" : "";

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Saved += () =>
        {
            _settingsWindow!.ApplyTo(_settings);
            SaveSettings();
            if (_settings.AutostartEnabled)
                AutostartManager.Enable();
            else
                AutostartManager.Disable();
            _iconKey = null; // thresholds may have moved the color bands
            _polling.TriggerNow();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void UpdateSignInItem(PollState state)
    {
        var needed = state.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;
        _signInItem.Header = state.IsBootstrapped
            ? "Sign in to Claude... (using Claude Code's session)"
            : "Sign in to Claude...";
        // NativeMenuItem has no bold-font affordance across every backend;
        // the header text change above is what actually shows in a native
        // menu, so the "needed" flag currently only documents intent.
        _ = needed;
    }

    private void ShowSignIn()
    {
        if (_signInWindow is not null)
        {
            _signInWindow.Activate();
            return;
        }

        var coordinator = new SignInCoordinator(_exchanger, _sessionStore);
        _signInWindow = new SignInWindow(coordinator);
        _signInWindow.SignedIn += _ => _polling.TriggerNow();
        _signInWindow.Closed += (_, _) => _signInWindow = null;
        _signInWindow.Show();
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    private void ExitApplication()
    {
        Log.Info("ClaudeCounter exiting.");
        IsExiting = true;
        _lifetime.Cancel();
        // Not: _trayIcon.IsVisible = false. Toggling it here races Avalonia's
        // own D-Bus tray-icon teardown and surfaces a TaskCanceledException
        // from DBusTrayIconImpl.WatchAsync() through the dispatcher on the way
        // out; Shutdown() below tears the tray icon down on its own.
        _polling.Dispose();
        _sleepResume.Dispose();
        _usageClient.Dispose();
        _refresher.Dispose();
        _exchanger.Dispose();
        _tokenEndpoint.Dispose();
        _updates.Dispose();
        _lifetime.Dispose();
        _desktop.Shutdown();
    }
}
