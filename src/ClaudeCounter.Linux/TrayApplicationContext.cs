using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ClaudeBackup;
using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Notifications;
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
    private readonly ThresholdTracker _tracker;
    private readonly SleepResumeWatcher _sleepResume = new();
    private readonly FlyoutWindow _flyout;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _signInItem;
    private readonly NativeMenuItem _switchAccountItem;
    private readonly NativeMenuItem _updateItem;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PosixSignalRegistration _sigTerm;
    private readonly PosixSignalRegistration _sigInt;

    // How long a signal-driven exit may take before the watchdog forces it.
    // Well inside systemd's 90 s stop timeout for the session's app units,
    // so a wedged UI thread can no longer hold up logout or shutdown.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);
    private int _exitWatchdogStarted;
    private bool _resourcesReleased;

    /// <summary>Set the moment Exit is chosen, so a stray cancellation from
    /// Avalonia's own teardown on the way out reads as "exiting", not a crash.</summary>
    public static bool IsExiting { get; private set; }

    private SettingsWindow? _settingsWindow;
    private SignInWindow? _signInWindow;
    private WindowIcon? _currentIcon;
    private (string Text, Band Band, bool Badge)? _iconKey;
    private bool _updateCheckStarted;
    private string? _updateUrl;
    // The signed-in account, for "Signed in as ..." - read from the session
    // store off the UI thread (a locked keyring can block), never per poll.
    private string? _accountEmail;
    private PollState? _lastState;

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
        else
        {
            if (_settings.AutostartEnabled)
                AutostartManager.EnsurePathCurrent();

            // Normalize() (inside Load()) may have just migrated the alert
            // dedupe state in memory; persist that now rather than on some
            // unrelated later save. Same as the Windows build.
            if (_settings.NotificationStateJustMigrated)
                _settingsStore.Save(_settings);
        }

        _refresher = new OAuthTokenRefresher(_tokenEndpoint);
        _exchanger = new OAuthCodeExchanger(_tokenEndpoint);

        _tracker = new ThresholdTracker(_settings.NotificationState);

        _polling = new PollingService(
            new TokenProvider(_sessionStore, _refresher),
            _usageClient, () => _settings.PollIntervalMinutes);

        _flyout = new FlyoutWindow(() => _settings);
        _flyout.RefreshRequested += () => _polling.TriggerNow();
        _flyout.UpdateRequested += OpenUpdatePage;
        _flyout.SignInRequested += ShowSignIn;

        _signInItem = new NativeMenuItem("Sign in to Claude...");
        _signInItem.Click += (_, _) => ShowSignIn();
        _switchAccountItem = new NativeMenuItem("Sign in with another account...") { IsVisible = false };
        _switchAccountItem.Click += (_, _) => ShowSignIn();

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
            _switchAccountItem,
            settingsItem,
        };
        // Only when the backup worker is installed, as on Windows.
        if (BackupTaskManager.WorkerAvailable())
        {
            var backupItem = new NativeMenuItem("Back up now");
            backupItem.Click += async (_, _) => await RunBackupNowAsync();
            menu.Items.Add(backupItem);
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

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

        // Without this, the process does not exit on SIGTERM - confirmed
        // directly (a bare Avalonia app, even with a TrayIcon and a
        // long-lived child process, exits fine on SIGTERM by default; this
        // app specifically did not, for a reason isolation did not pin down).
        // That is exactly what a desktop session manager sends on logout or
        // shutdown, so an unresponsive process here can stall or block it
        // entirely - reported against this port on KDE/Fedora. Cancel the
        // signal's default handling and drive our own clean shutdown instead.
        _sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleTerminationSignal);
        _sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleTerminationSignal);

        // Every way out - the Exit menu item, a signal, or the desktop's own
        // logout/shutdown request via X11 session management, which never
        // goes through ExitApplication at all - ends with the lifetime
        // raising Exit, so teardown hangs off that one event.
        _desktop.Exit += (_, _) => ReleaseResources();

        Log.Info($"ClaudeCounter {AppInfo.DisplayVersion} started (Linux).");

        // Shells out to dbus-send with a timeout of a few seconds; never on
        // the UI thread, which would freeze the tray icon at startup.
        _ = Task.Run(TrayPresence.WarnIfMissing);

        MaybeShowOnboarding();
        _ = RefreshAccountEmailAsync();
    }

    private void OnResumed()
    {
        Log.Info("System resumed from sleep - refreshing now.");
        _polling.TriggerNow();
    }

    // Runs on a thread pool thread, not the UI thread - marshal before
    // touching anything Avalonia owns. Cancelling here means the process
    // does not die from the signal's default disposition mid-cleanup; it
    // exits instead once ExitApplication's own Shutdown() completes.
    //
    // Cancelling also means that if the UI thread never runs the posted
    // ExitApplication - the display or session bus already gone at logout,
    // or the UI thread stuck - nothing else would end the process, and
    // systemd would wait out its full stop timeout before killing it. The
    // watchdog bounds that.
    private void HandleTerminationSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Log.Info($"Received {context.Signal} - exiting.");
        StartExitWatchdog();
        Dispatcher.UIThread.Post(ExitApplication);
    }

    private void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) == 1)
            return;

        // Background thread: it never keeps the process alive by itself, so
        // a normal exit within the grace period simply abandons it.
        new Thread(() =>
        {
            Thread.Sleep(ExitGrace);
            Log.Warn($"Clean shutdown did not finish within {ExitGrace.TotalSeconds:0} s - forcing exit.");

            // Environment.Exit runs ProcessExit handlers, which could block
            // on the same wedged state; if it has not returned shortly, stop
            // the process outright.
            new Thread(() => Environment.Exit(0)) { IsBackground = true, Name = "Forced exit" }.Start();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true,
            Name = "Exit watchdog",
        }.Start();
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
        wizard.SignInCompleted += () =>
        {
            _polling.TriggerNow();
            _ = RefreshAccountEmailAsync();
        };
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
        // The click that hid it (focus moved to the panel) was meant to close it.
        if (_flyout.RecentlyDismissed)
            return;
        _flyout.ShowNearTray();
    }

    private void OnPollUpdated(PollState state)
    {
        // Backup health rides along with each usage poll, as on Windows: no
        // separate timer, and it is re-read from backup-status.json, which
        // the worker rewrites after every run.
        var backupHealth = EvaluateBackupHealth();

        UpdateIcon(state, backupHealth);
        _trayIcon.ToolTipText = BuildTooltip(state, backupHealth);
        _flyout.UpdateState(state);
        _flyout.UpdateBackupHealth(backupHealth);
        UpdateSignInItem(state);

        if (!_updateCheckStarted && state.Problem == ProblemKind.None)
        {
            _updateCheckStarted = true;
            _ = CheckForUpdatesAsync();
        }

        EvaluateAlerts(state);
        EvaluateBackupHealthNotification(backupHealth);
    }

    /// <summary>Null when the backup worker is not installed.</summary>
    private static BackupHealthResult? EvaluateBackupHealth()
    {
        if (!BackupTaskManager.WorkerAvailable())
            return null;
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        var status = BackupStatus.Load(BackupStatus.DefaultPath());
        return BackupHealth.Evaluate(status, config, DateTimeOffset.UtcNow, config.Schedule.BackupStaleAfterDays);
    }

    /// <summary>
    /// Pops up once on a transition into a problem state (see
    /// BackupHealthPresenter.ShouldNotify), held back while a dialog is open,
    /// and remembers every observed state so a later failure re-arms after a
    /// recovery. Mirrors the Windows build.
    /// </summary>
    private void EvaluateBackupHealthNotification(BackupHealthResult? result)
    {
        if (result is null)
            return;

        var previous = _settings.LastBackupHealthState;
        if (BackupHealthPresenter.ShouldNotify(previous, result.State))
        {
            var dialogOpen = !_settings.OnboardingCompleted || _settingsWindow is not null || _signInWindow is not null;
            if (dialogOpen)
            {
                Log.Info($"Backup health alert suppressed (dialog open): {result.State}.");
            }
            else
            {
                Log.Info($"Backup health alert: {result.State}.");
                try
                {
                    AlertPopupWindow.ShowBackupHealth(result, _settings.PopupPlacement, _settings.PopupAutoDismissSeconds);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Backup health popup failed: {ex.Message}");
                }
            }
        }

        if (previous != result.State)
        {
            _settings.LastBackupHealthState = result.State;
            SaveSettings();
        }
    }

    /// <summary>
    /// Runs the worker and reports the result as a desktop notification (the
    /// Windows build's balloon tip) - a failing "Back up now" must not look
    /// like a successful one.
    /// </summary>
    private async Task RunBackupNowAsync()
    {
        var exitCode = await BackupTaskManager.RunNowAsync();
        var message = BackupTaskManager.ResultMessage(exitCode);
        Log.Info($"Back up now: {message}");
        Shell.Notify("ClaudeCounter Backup", message);
        _polling.TriggerNow(); // refresh the badge/tooltip from the new status
    }

    /// <summary>
    /// Same rules as the Windows build: ThresholdTracker decides which
    /// crossings are new (once per crossing per window, re-armed on reset,
    /// optionally repeated), the per-level toggles decide which are shown.
    /// </summary>
    private void EvaluateAlerts(PollState state)
    {
        if (state.Problem != ProblemKind.None || state.Snapshot is not { } snapshot)
            return;

        var events = _tracker.Evaluate(snapshot, _settings, DateTimeOffset.UtcNow);
        if (events.Count == 0)
            return;

        // While a dialog is open (first-run wizard, Settings, Sign in) a
        // popup - a centered one takes focus - would land on top of what the
        // user is doing. The tracker has still consumed the crossing and the
        // state is still saved below, so it does not fire late either; it is
        // just not shown. Mirrors the Windows build.
        var dialogOpen = !_settings.OnboardingCompleted
            || _settingsWindow is not null
            || _signInWindow is not null;

        foreach (var e in events)
        {
            var enabled = e.Level switch
            {
                AlertLevel.Maxed => _settings.MaxedAlertsEnabled,
                AlertLevel.Critical => _settings.CriticalAlertsEnabled,
                AlertLevel.Warn => _settings.WarnAlertsEnabled,
                _ => false,
            };
            if (!enabled)
                continue;
            if (dialogOpen)
            {
                Log.Info($"Alert suppressed (dialog open): {e.WindowKey} {e.Level} at {e.Utilization:0}%.");
                continue;
            }
            Log.Info($"Alert: {e.WindowKey} {e.Level} at {e.Utilization:0}%.");
            try
            {
                AlertPopupWindow.Show(e, _settings.PopupPlacement, _settings.PopupAutoDismissSeconds);
            }
            catch (Exception ex)
            {
                Log.Warn($"Alert popup failed: {ex.Message}");
            }
        }

        // Persist dedupe state whether or not a popup was shown, so a disabled
        // level does not re-fire on every later poll.
        SaveSettings();
    }

    private void UpdateIcon(PollState state, BackupHealthResult? backupHealth)
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
        SetIcon(text, band, backupHealth?.State.WarrantsAttention() ?? false);
    }

    private void SetIcon(string text, Band band, bool badge = false)
    {
        if (_iconKey == (text, band, badge))
            return;
        _currentIcon = IconRenderer.Render(text, band, badge);
        _trayIcon.Icon = _currentIcon;
        _iconKey = (text, band, badge);
    }

    private static string BuildTooltip(PollState state, BackupHealthResult? backupHealth)
    {
        var usage = BuildUsageTooltip(state);
        // A backup problem is one extra line, never a number - see
        // BackupHealthPresenter.TooltipLine.
        return BackupHealthPresenter.TooltipLine(backupHealth) is { } backupLine
            ? usage + "\n" + backupLine
            : usage;
    }

    private static string BuildUsageTooltip(PollState state)
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
        _settingsWindow.BackupDestinationsChanged += () => _polling.TriggerNow();
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

    /// <summary>
    /// The first menu entry says who is signed in, rather than always
    /// offering "Sign in to Claude...":
    /// <list type="bullet">
    /// <item>ClaudeCounter's own session - "Signed in as you@example.com"
    /// (or "Signed in to Claude" for a session saved before the email was
    /// recorded), greyed out, plus "Sign in with another account...".</item>
    /// <item>Claude Code's session - the sign-in prompt, noting the fallback.</item>
    /// <item>The environment-variable token - says so, greyed out.</item>
    /// <item>Nothing usable - "Sign in to Claude...".</item>
    /// </list>
    /// </summary>
    private void UpdateSignInItem(PollState state)
    {
        _lastState = state;
        var signInNeeded = state.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;
        var (header, enabled, showSwitch) = SignInMenu.For(state.Source, signInNeeded, _accountEmail);
        _signInItem.Header = header;
        _signInItem.IsEnabled = enabled;
        _switchAccountItem.IsVisible = showSwitch;
    }

    private async Task RefreshAccountEmailAsync()
    {
        string? email;
        try
        {
            email = await Task.Run(() => _sessionStore.Read()?.AccountEmail);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not read the signed-in account: {e.Message}");
            return;
        }
        _accountEmail = email;
        if (_lastState is { } state)
            UpdateSignInItem(state);
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
        _signInWindow.SignedIn += session =>
        {
            _accountEmail = session.AccountEmail;
            _polling.TriggerNow();
        };
        _signInWindow.Closed += (_, _) => _signInWindow = null;
        _signInWindow.Show();
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    private void ExitApplication()
    {
        // Idempotent: a signal and the Exit menu item (or two signals) could
        // both reach here, and every disposal below only tolerates once.
        if (IsExiting)
            return;
        IsExiting = true;

        Log.Info("ClaudeCounter exiting.");
        // Raises Exit, which runs ReleaseResources. Forced, so the flyout's
        // "only hide" close handler cannot veto it.
        _desktop.Shutdown();
    }

    /// <summary>
    /// Idempotent teardown, reached from the lifetime's Exit event on every
    /// exit path (see the constructor).
    /// </summary>
    private void ReleaseResources()
    {
        if (_resourcesReleased)
            return;
        _resourcesReleased = true;
        if (!IsExiting)
            Log.Info("Desktop session is ending (logout/shutdown) - exiting.");
        IsExiting = true;

        _lifetime.Cancel();
        _sigTerm.Dispose();
        _sigInt.Dispose();
        // Not: _trayIcon.IsVisible = false. Toggling it here races Avalonia's
        // own D-Bus tray-icon teardown and surfaces a TaskCanceledException
        // from DBusTrayIconImpl.WatchAsync() through the dispatcher on the way
        // out; the lifetime tears the tray icon down on its own.
        _polling.Dispose();
        _sleepResume.Dispose();
        _usageClient.Dispose();
        _refresher.Dispose();
        _exchanger.Dispose();
        _tokenEndpoint.Dispose();
        _updates.Dispose();
        _lifetime.Dispose();
    }
}
