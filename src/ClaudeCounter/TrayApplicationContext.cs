using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Settings;
using ClaudeCounter.UI;

namespace ClaudeCounter;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const int MaxTooltipLength = 127;
    private static readonly TimeSpan ReToggleGuard = TimeSpan.FromMilliseconds(300);

    private readonly NotifyIcon _notifyIcon;
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    // One endpoint, shared by the refresher and the code exchanger, so the app
    // keeps a single connection pool to Anthropic's token service.
    private readonly OAuthTokenEndpoint _tokenEndpoint = new();
    private readonly OAuthTokenRefresher _refresher;
    private readonly OAuthCodeExchanger _exchanger;

    // ONE instance, shared by the poll loop and every sign-in dialog. Two
    // stores over the same file would race on read-your-own-write: the dialog
    // writes a session the loop has already cached as absent.
    private readonly ISessionStore _sessionStore = new EncryptedSessionStore();

    private readonly UsageClient _usageClient = new();
    private readonly UpdateChecker _updates = new();
    private readonly PollingService _polling;
    private readonly FlyoutForm _flyout;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _signInItem;
    private readonly CancellationTokenSource _lifetime = new();

    private SettingsForm? _settingsForm;
    private AboutForm? _aboutForm;
    private SignInForm? _signInForm;
    private Icon? _currentIcon;
    private (string Text, Band Band)? _iconKey;
    private bool _updateCheckStarted;
    private string? _updateUrl;
    private System.Windows.Forms.Timer? _onboardingTimer;

    public TrayApplicationContext()
    {
        bool isFirstRun;
        (_settings, isFirstRun) = _settingsStore.Load();
        if (isFirstRun)
        {
            _settingsStore.Save(_settings); // materialize defaults
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

        _flyout = new FlyoutForm(() => _settings);
        _flyout.RefreshRequested += () => _polling.TriggerNow();
        _flyout.UpdateRequested += OpenUpdatePage;
        _flyout.SignInRequested += ShowSignIn;

        _updateItem = new ToolStripMenuItem("Update available", null, (_, _) => OpenUpdatePage())
        {
            Visible = false,
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };

        _signInItem = new ToolStripMenuItem("Sign in to Claude...", null, (_, _) => ShowSignIn());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => _polling.TriggerNow());
        menu.Items.Add(_signInItem);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add("Open log folder", null, (_, _) => Shell.ShowLogFolder());
        menu.Items.Add("About ClaudeCounter...", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        SetIcon("--", Band.Gray);
        _notifyIcon.Text = "ClaudeCounter - waiting for first update";
        _notifyIcon.MouseClick += OnTrayClick;

        _polling.Updated += OnPollUpdated;
        _polling.Start();

        // The poll loop's Task.Delay timer is frozen while the machine sleeps, so
        // on wake the tray shows stale data until the (delayed) timer fires. Force
        // an immediate refresh on resume instead.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Log.Info($"ClaudeCounter {AppInfo.DisplayVersion} started.");

        MaybeShowOnboarding();
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
        if (_sessionStore.Read() is not null)
        {
            _settings.OnboardingCompleted = true;
            SaveSettings();
            return;
        }
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenProvider.EnvVarName)))
        {
            _settings.OnboardingCompleted = true;
            SaveSettings();
            return;
        }

        // Deferred until the message loop is running: calling ShowDialog from
        // the constructor blocks Application.Run, so the tray icon would not
        // exist yet and the wizard would be the app's only visible window.
        //
        // A one-shot timer rather than Control.BeginInvoke: nothing this class
        // owns has a window handle yet - the flyout is not shown until the user
        // clicks the tray icon - and BeginInvoke on a handle-less control
        // throws, which crashed the app on first run before anything was logged.
        _onboardingTimer = new System.Windows.Forms.Timer { Interval = 1 };
        _onboardingTimer.Tick += (_, _) =>
        {
            _onboardingTimer.Stop();
            ShowOnboarding();
        };
        _onboardingTimer.Start();
    }

    private void ShowOnboarding()
    {
        _onboardingTimer?.Dispose();
        _onboardingTimer = null;

        var coordinator = new SignInCoordinator(_exchanger, _sessionStore);
        using var wizard = new OnboardingForm(coordinator, _settings);

        // Poll as soon as they sign in rather than waiting for the wizard to
        // close. ShowDialog runs a nested message loop, so the poll loop's
        // continuations still run and the tray icon updates behind the wizard -
        // which is what the sign-in step now tells the user to look for.
        wizard.SignInCompleted += () => _polling.TriggerNow();

        wizard.ShowDialog();

        _settings.Normalize();
        SaveSettings();
        if (_settings.AutostartEnabled)
            AutostartManager.Enable();
        else
            AutostartManager.Disable();

        Log.Info($"Onboarding finished. Signed in: {wizard.SignedIn}.");
        _polling.TriggerNow();
    }

    /// <summary>
    /// Runs at most once a day, on the poll loop's thread, after the app is
    /// already showing data. Deliberately never a dialog: a tray app that
    /// interrupts you on login to talk about itself is an annoyance.
    /// </summary>
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
        _updateItem.Text = $"Update available: v{available.Version}";
        _updateItem.Visible = true;
        _flyout.ShowUpdateAvailable(available.Version);
    }

    private void OpenUpdatePage() => Shell.OpenUrl(_updateUrl ?? AppInfo.ReleasesUrl);

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            return;
        Log.Info("System resumed from sleep - refreshing now.");
        _polling.TriggerNow();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        if (_flyout.Visible)
        {
            _flyout.Hide();
            return;
        }
        // Clicking the icon while the flyout is open fires Deactivate (hide)
        // before this click arrives; do not instantly re-open it.
        if (DateTime.UtcNow - _flyout.HiddenAtUtc < ReToggleGuard)
            return;
        _flyout.ShowNear(Cursor.Position);
    }

    private void OnPollUpdated(PollState state)
    {
        UpdateIcon(state);
        var tooltip = BuildTooltip(state);
        _notifyIcon.Text = tooltip;
        _flyout.UpdateState(state);
        UpdateSignInItem(state);
        Log.Info($"Tooltip: {tooltip.Replace("\n", " | ")}");

        // Piggyback on the first good poll rather than the constructor: no
        // startup delay, and no pointless GitHub call on a machine that has no
        // network anyway.
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
                : Band.Gray; // stale: show last known value, dimmed
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
        var icon = IconRenderer.Render(text, band);
        var previous = _currentIcon;
        _notifyIcon.Icon = icon;
        _currentIcon = icon;
        _iconKey = (text, band);
        previous?.Dispose();
    }

    private string BuildTooltip(PollState state)
    {
        string tooltip;
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
            tooltip = string.Join("\n", parts);
        }
        else
        {
            tooltip = "ClaudeCounter\n" + (state.ProblemMessage ?? "Waiting for first update");
        }
        return tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];
    }

    private static string ResetSuffix(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } at ? $", resets in {TimeText.Countdown(at, now)}" : "";

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings);
        if (_settingsForm.ShowDialog() == DialogResult.OK)
        {
            _settingsForm.ApplyTo(_settings);
            SaveSettings();
            if (_settings.AutostartEnabled)
                AutostartManager.Enable();
            else
                AutostartManager.Disable();
            _iconKey = null; // thresholds may have moved the color bands
            _polling.TriggerNow();
        }
        _settingsForm.Dispose();
        _settingsForm = null;
    }

    /// <summary>
    /// Draws attention to sign-in when there is nothing else the user can do,
    /// and stays out of the way otherwise.
    /// </summary>
    private void UpdateSignInItem(PollState state)
    {
        var needed = state.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;
        var baseFont = SystemFonts.MenuFont ?? Control.DefaultFont;

        _signInItem.Font = needed ? new Font(baseFont, FontStyle.Bold) : baseFont;
        _signInItem.Text = state.IsBootstrapped
            ? "Sign in to Claude... (using Claude Code's session)"
            : "Sign in to Claude...";
    }

    private void ShowSignIn()
    {
        if (_signInForm is { IsDisposed: false })
        {
            _signInForm.Activate();
            return;
        }

        // A fresh coordinator per dialog: each one owns a single PKCE attempt.
        // The session store is the shared instance, so whatever is written here
        // is what the poll loop reads next tick.
        var coordinator = new SignInCoordinator(_exchanger, _sessionStore);

        _signInForm = new SignInForm(coordinator);
        _signInForm.ShowDialog();
        var signedIn = _signInForm.Session is not null;
        _signInForm.Dispose();
        _signInForm = null;

        if (signedIn)
            _polling.TriggerNow();
    }

    private void ShowAbout()
    {
        if (_aboutForm is { IsDisposed: false })
        {
            _aboutForm.Activate();
            return;
        }
        _aboutForm = new AboutForm(_updates);
        _aboutForm.ShowDialog();
        _aboutForm.Dispose();
        _aboutForm = null;
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    private void ExitApplication()
    {
        Log.Info("ClaudeCounter exiting.");
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _onboardingTimer?.Dispose();
        _lifetime.Cancel();
        // Dispose the icon before exiting or a ghost icon lingers until mouse-over.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _polling.Dispose();
        _usageClient.Dispose();
        _refresher.Dispose();
        _exchanger.Dispose();
        _tokenEndpoint.Dispose();
        _updates.Dispose();
        _flyout.Dispose();
        _lifetime.Dispose();
        ExitThread();
    }
}
