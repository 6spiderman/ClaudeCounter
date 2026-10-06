using ClaudeBackup;
using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Notifications;
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
    private readonly ISessionStore _sessionStore = new EncryptedSessionStore(null, new DpapiDataProtector());

    private readonly UsageClient _usageClient = new();
    private readonly UpdateChecker _updates = new();
    private readonly ThresholdTracker _tracker;
    private readonly PollingService _polling;
    private readonly FlyoutForm _flyout;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _signInItem;
    private readonly ToolStripMenuItem _switchAccountItem;
    private readonly ToolStripMenuItem _signOutItem;
    private InstanceChannel? _channel;
    private readonly CancellationTokenSource _lifetime = new();

    private SettingsForm? _settingsForm;
    private AboutForm? _aboutForm;
    private SignInForm? _signInForm;
    private Icon? _currentIcon;
    // S11b: badge is now part of the key - see SetIcon's own remarks - or a
    // backup-health change alone (no usage-text/band change) would never
    // trigger a re-render.
    private (string Text, Band Band, bool Badge)? _iconKey;
    private bool _updateCheckStarted;
    private bool _shutdownDone;
    private string? _updateUrl;
    // The signed-in account, for "Signed in as ..." (see SignInMenu). Read
    // once at startup and updated on every sign-in, not per poll.
    private string? _accountEmail;
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
        else
        {
            if (_settings.AutostartEnabled)
                AutostartManager.EnsurePathCurrent();

            // Normalize() (called inside Load()) may have just bumped
            // NotificationStateVersion and cleared stale dedupe state in
            // memory. Persist that immediately rather than waiting for some
            // unrelated later SaveSettings() call - Normalize() itself does
            // no I/O, so this is the one place that decision belongs.
            if (_settings.NotificationStateJustMigrated)
                _settingsStore.Save(_settings);
        }

        _refresher = new OAuthTokenRefresher(_tokenEndpoint);
        _exchanger = new OAuthCodeExchanger(_tokenEndpoint);

        _tracker = new ThresholdTracker(_settings.NotificationState);

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
        _switchAccountItem = new ToolStripMenuItem("Sign in with another account...", null, (_, _) => ShowSignIn()) { Visible = false };
        _signOutItem = new ToolStripMenuItem("Sign out", null, (_, _) => SignOut()) { Visible = false };
        _accountEmail = ReadAccountEmail();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => _polling.TriggerNow());
        menu.Items.Add(_signInItem);
        menu.Items.Add(_switchAccountItem);
        menu.Items.Add(_signOutItem);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        if (BackupTaskManager.WorkerAvailable())
            menu.Items.Add("Back up now", null, async (_, _) => await RunBackupNowAsync());
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
        SetIcon("--", Band.Gray, badge: false);
        _notifyIcon.Text = "ClaudeCounter - waiting for first update";
        _notifyIcon.MouseClick += OnTrayClick;

        _polling.Updated += OnPollUpdated;
        _polling.Start();

        // The poll loop's Task.Delay timer is frozen while the machine sleeps, so
        // on wake the tray shows stale data until the (delayed) timer fires. Force
        // an immediate refresh on resume instead.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Close cleanly whoever asks: Windows ending the session, or the
        // installer's Restart Manager closing us for an upgrade. Without these
        // the tray icon is never disposed on those paths and a ghost lingers.
        //
        // Create the flyout's window handle up front, here on the UI thread.
        // It is never shown until the user clicks the tray icon, but it is the
        // marshaling target OnSessionEnding posts the exit to, and BeginInvoke
        // requires a handle that already exists.
        _ = _flyout.Handle;
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += OnApplicationExit;

        Log.Info($"ClaudeCounter {AppInfo.DisplayVersion} started.");

        // Lets a second launch bring up this instance's flyout, and
        // `ClaudeCounter.exe --refresh` ask for a poll. Commands arrive on a
        // pool thread; _flyout's handle (created above) is the marshaling
        // target, as for OnSessionEnding.
        _channel = InstanceChannel.StartServer(command => _flyout.BeginInvoke(new Action(() => OnChannelCommand(command))));

        MaybeShowOnboarding();
    }

    private void OnChannelCommand(string command)
    {
        switch (command)
        {
            case InstanceChannel.Show:
                if (_flyout.Visible)
                {
                    _flyout.Activate();
                }
                else
                {
                    // Not the cursor: a launch from the Start menu leaves it
                    // anywhere. The tray's corner of the primary screen, where
                    // alert popups go too.
                    var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
                    _flyout.ShowNear(new Point(area.Right - 8, area.Bottom));
                }
                break;
            case InstanceChannel.Refresh:
                _polling.TriggerNow();
                break;
        }
    }

    /// <summary>
    /// Forgets ClaudeCounter's own login after a confirmation. The poll that
    /// follows falls back to Claude Code's login if there is one, otherwise
    /// asks to sign in - TokenProvider's normal order.
    /// </summary>
    private void SignOut()
    {
        var answer = MessageBox.Show(SignInMenu.SignOutConfirmation, "ClaudeCounter",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            _sessionStore.Clear();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Sign out: clearing the stored session failed: {e.Message}");
        }

        _accountEmail = null;
        Log.Info("Signed out.");
        _polling.ResetForSignOut();
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
        wizard.SignInCompleted += () =>
        {
            _accountEmail = ReadAccountEmail();
            _polling.TriggerNow();
        };

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
        // S11b: computed once per poll and threaded through the icon/tooltip/
        // flyout below - loaded fresh from disk every time (like
        // BackupTaskManager.RunNowAsync and SettingsForm.OnOpenRestoreDialog
        // already do for backup.json), never cached, so a change made from
        // the Settings dialog or an in-progress worker run updating
        // backup-status.json is picked up on the very next poll with no
        // extra invalidation plumbing. Every existing line below this point
        // is unchanged in what it does - see UpdateIcon/BuildTooltip's own
        // remarks for exactly how backupHealth is folded in.
        var backupHealth = EvaluateBackupHealth();

        UpdateIcon(state, backupHealth);
        var tooltip = BuildTooltip(state, backupHealth);
        _notifyIcon.Text = tooltip;
        _flyout.UpdateState(state);
        _flyout.UpdateBackupHealth(backupHealth);
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

        EvaluateAlerts(state);

        EvaluateBackupHealthNotification(backupHealth);
    }

    /// <summary>
    /// S11b: loads backup.json and backup-status.json fresh from disk and
    /// evaluates health via BackupHealth.Evaluate. Returns null when the
    /// backup worker is not installed (BackupTaskManager.WorkerAvailable()) -
    /// the feature does not exist at all in that case, so no badge, no
    /// tooltip/flyout line, and no popup (every consumer below treats a null
    /// result exactly like NotConfigured). Safe to call unguarded on the poll
    /// timer: both Load calls degrade to safe defaults (BackupConfig.Load
    /// falls back to Default(), BackupStatus.Load falls back to a fresh
    /// status that reads as NeverRun) rather than throwing - see both types'
    /// own doc comments, which call out this exact caller.
    /// </summary>
    private BackupHealthResult? EvaluateBackupHealth()
    {
        if (!BackupTaskManager.WorkerAvailable())
            return null;
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        var status = BackupStatus.Load(BackupStatus.DefaultPath());
        return BackupHealth.Evaluate(status, config, DateTimeOffset.UtcNow, config.Schedule.BackupStaleAfterDays);
    }

    /// <summary>
    /// S11b: one popup per transition into a problem state - see
    /// BackupHealthPresenter.ShouldNotify, the pure decision this method
    /// wraps with I/O (settings persistence) and UI (the popup itself, and
    /// the same modal-dialog suppression EvaluateAlerts already applies to
    /// usage alerts, for the same reason: a popup created while a modal
    /// dialog is running would yank focus off it). <paramref
    /// name="result"/> null means the worker is not installed - nothing to
    /// evaluate, nothing to persist.
    ///
    /// _settings.LastBackupHealthState tracks the last OBSERVED state, not
    /// just the last one that actually popped up - it is advanced whenever
    /// the state changes, even when a popup for that change was suppressed by
    /// a modal dialog, mirroring EvaluateAlerts' own "still persist state
    /// even when suppressed, so the alert does not fire late once the dialog
    /// closes" behaviour for usage alerts.
    /// </summary>
    private void EvaluateBackupHealthNotification(BackupHealthResult? result)
    {
        if (result is null)
            return;

        var previous = _settings.LastBackupHealthState;
        if (BackupHealthPresenter.ShouldNotify(previous, result.State))
        {
            var modalActive = !_settings.OnboardingCompleted
                || _settingsForm is { IsDisposed: false }
                || _signInForm is { IsDisposed: false }
                || _aboutForm is { IsDisposed: false };
            if (modalActive)
            {
                Log.Info($"Backup health alert suppressed (modal dialog open): {result.State}.");
            }
            else
            {
                Log.Info($"Backup health alert: {result.State}.");
                try
                {
                    AlertPopupForm.ShowBackupHealth(result, _settings.PopupPlacement, Cursor.Position, _settings.PopupAutoDismissSeconds);
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

    private void EvaluateAlerts(PollState state)
    {
        if (state.Problem != ProblemKind.None || state.Snapshot is not { } snapshot)
            return;

        var events = _tracker.Evaluate(snapshot, _settings, DateTimeOffset.UtcNow);
        if (events.Count == 0)
            return;

        // A modal dialog (onboarding, settings, sign-in, about) runs its own
        // nested message loop, and poll continuations still run underneath it.
        // A popup created then is not disabled by that loop and is TopMost - a
        // Maxed popup even calls Activate() - so it would yank focus off the
        // dialog the user is in the middle of. Still let the tracker consume
        // the crossing and still persist state below, so the alert does not
        // fire late once the dialog closes; just suppress showing it now.
        var modalActive = !_settings.OnboardingCompleted
            || _settingsForm is { IsDisposed: false }
            || _signInForm is { IsDisposed: false }
            || _aboutForm is { IsDisposed: false };

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
            if (modalActive)
            {
                Log.Info($"Alert suppressed (modal dialog open): {e.WindowKey} {e.Level} at {e.Utilization:0}%.");
                continue;
            }
            Log.Info($"Alert: {e.WindowKey} {e.Level} at {e.Utilization:0}%.");
            try
            {
                AlertPopupForm.Show(e, _settings.PopupPlacement, Cursor.Position, _settings.PopupAutoDismissSeconds);
            }
            catch (Exception ex)
            {
                Log.Warn($"Alert popup failed: {ex.Message}");
            }
        }

        // Persist dedupe state whether or not a popup was shown, so a disabled
        // alert level does not re-fire on every later poll.
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
                : Band.Gray; // stale: show last known value, dimmed
        }
        else
        {
            text = "--";
            band = Band.Gray;
        }
        // S11b: keyed off WarrantsAttention() directly - never a hand-rolled
        // Failed-or-Stale check (see BackupHealthPresenter's own remarks).
        SetIcon(text, band, backupHealth?.State.WarrantsAttention() ?? false);
    }

    private void SetIcon(string text, Band band, bool badge)
    {
        if (_iconKey == (text, band, badge))
            return;
        var icon = IconRenderer.Render(text, band, badge);
        var previous = _currentIcon;
        _notifyIcon.Icon = icon;
        _currentIcon = icon;
        _iconKey = (text, band, badge);
        previous?.Dispose();
    }

    private string BuildTooltip(PollState state, BackupHealthResult? backupHealth)
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

        // S11b: appended LAST, after the guaranteed Session/Week lines above -
        // so if the 127-char clamp below has to cut anything, it cuts this
        // line, never a usage number (see BackupHealthPresenter.TooltipLine's
        // doc comment and the design spec's tooltip constraint).
        if (BackupHealthPresenter.TooltipLine(backupHealth) is { } backupLine)
            tooltip += "\n" + backupLine;

        return tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];
    }

    private static string ResetSuffix(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } at ? $", resets in {TimeText.Countdown(at, now)}" : "";

    /// <summary>
    /// I1: "Back up now" must surface its result instead of firing the
    /// worker and forgetting about it - a failing backup previously looked
    /// exactly like a successful one. Awaiting here does not block the UI
    /// thread: BackupTaskManager.RunNowAsync awaits WaitForExitAsync, which
    /// yields back to the message loop while the worker runs.
    /// </summary>
    private async Task RunBackupNowAsync()
    {
        var exitCode = await BackupTaskManager.RunNowAsync();
        var message = BackupTaskManager.ResultMessage(exitCode);
        Log.Info($"Back up now: {message}");
        _notifyIcon.ShowBalloonTip(
            4000, "ClaudeCounter Backup", message,
            exitCode == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings);
        // S17c: BackupDestinationsDialog (opened from the Backup tab's
        // "Manage destinations..." button) saves each add/edit/remove
        // immediately, independent of this dialog's own OK/Cancel - so a
        // removed (or newly broken) destination must be reflected in the
        // tray's badge/tooltip/popup PROMPTLY, not only the next time
        // Settings happens to close with OK (which is also when this used
        // to run) or up to PollIntervalMinutes later at the next scheduled
        // poll. TriggerNow() wakes the poll loop immediately; PollOnceAsync
        // always calls Updated (see its own remarks) regardless of
        // sign-in/network state, so OnPollUpdated's EvaluateBackupHealth
        // re-evaluation runs even when nothing about usage changed.
        _settingsForm.BackupDestinationsChanged += () => _polling.TriggerNow();
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
    /// Says who is signed in ("Signed in as ...", greyed out, plus "Sign in
    /// with another account...") instead of always offering sign-in, and
    /// draws attention to sign-in when there is nothing else the user can do.
    /// The wording is shared with the Linux tray - see SignInMenu.
    /// </summary>
    private void UpdateSignInItem(PollState state)
    {
        var needed = state.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;
        var baseFont = SystemFonts.MenuFont ?? Control.DefaultFont;
        var (header, enabled, showSwitch, showSignOut) = SignInMenu.For(state.Source, needed, _accountEmail);

        _signInItem.Font = needed ? new Font(baseFont, FontStyle.Bold) : baseFont;
        _signInItem.Text = header;
        _signInItem.Enabled = enabled;
        _switchAccountItem.Visible = showSwitch;
        _signOutItem.Visible = showSignOut;
    }

    private string? ReadAccountEmail()
    {
        try
        {
            return _sessionStore.Read()?.AccountEmail;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        if (_signInForm.Session is { } session)
            _accountEmail = session.AccountEmail;
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

    /// <summary>
    /// Releases everything the tray owns. Idempotent, because it is reachable
    /// from three directions: the Exit menu item, Windows ending the session
    /// (logoff or shutdown), and the installer's Restart Manager asking us to
    /// close for an upgrade. Only the first of those runs our own code path -
    /// the other two used to skip it entirely, which left a ghost tray icon
    /// sitting in the notification area until the user moused over it.
    /// </summary>
    private void Shutdown()
    {
        if (_shutdownDone)
            return;
        _shutdownDone = true;

        Log.Info("ClaudeCounter exiting.");
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        Application.ApplicationExit -= OnApplicationExit;
        _onboardingTimer?.Dispose();
        _channel?.Dispose();
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
    }

    private void OnApplicationExit(object? sender, EventArgs e) => Shutdown();

    /// <summary>
    /// Windows is ending the session, or the installer's Restart Manager is
    /// asking us to close for an upgrade. Restart Manager waits for the PROCESS
    /// to terminate, so we do have to exit - but NOT from this thread.
    ///
    /// SystemEvents raises this on its own dedicated thread while Windows is
    /// still waiting for that window procedure to return. Doing the work here
    /// deadlocks: disposing UI-thread-owned objects hangs, and even a bare
    /// Environment.Exit never completes. Both were observed - the log stopped
    /// mid-handler and the process sat alive until Setup gave up.
    ///
    /// So post the exit to the UI thread and return immediately. The UI thread
    /// runs the normal ExitApplication path (full disposal, tray icon removed,
    /// message loop ended), and the process exits on its own terms.
    /// </summary>
    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        Log.Info($"Session ending ({e.Reason}) - posting exit to the UI thread.");
        try
        {
            // _flyout's handle is created in the constructor precisely so this
            // marshaling target always exists, even though it is never shown.
            _flyout.BeginInvoke(new Action(ExitApplication));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not post the exit request: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        Shutdown();
        ExitThread();
    }
}
