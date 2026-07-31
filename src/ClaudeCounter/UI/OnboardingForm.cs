using ClaudeCounter.Core.Auth;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// First-run wizard: what the app does, sign in, set preferences. Step 2 hosts
/// the same <see cref="SignInPanel"/> the tray menu opens, so there is one
/// sign-in implementation rather than two.
/// </summary>
public sealed class OnboardingForm : Form
{
    private const int Steps = 3;

    private readonly AppSettings _settings;
    private readonly SignInPanel _signInPanel;
    private readonly Panel _host;
    private readonly Control[] _pages;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly LinkLabel _skipLink;

    private readonly ComboBox _intervalCombo;
    private readonly CheckBox _autostartCheck;

    private int _step;

    public bool SignedIn { get; private set; }

    /// <summary>
    /// Raised the moment sign-in succeeds, not when the wizard closes, so the
    /// tray icon can refresh while the user is still reading the step that
    /// tells them to go and look at it.
    /// </summary>
    public event Action? SignInCompleted;

    public OnboardingForm(SignInCoordinator coordinator, AppSettings settings)
    {
        _settings = settings;

        Text = "Welcome to ClaudeCounter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 430);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        _signInPanel = new SignInPanel(coordinator)
        {
            // The wizard stays open after this, so the default
            // "Fetching your usage..." would sit there looking stuck.
            SuccessMessage = "Signed in. Your usage is in the system tray now - " +
                             "look for the coloured icon by the clock.",
        };
        _signInPanel.StateChanged += Sync;

        _intervalCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        foreach (var minutes in AppSettings.IntervalPresets)
            _intervalCombo.Items.Add($"{minutes} min");
        _intervalCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, settings.PollIntervalMinutes));

        _autostartCheck = new CheckBox
        {
            Text = "Start ClaudeCounter when I sign in to Windows",
            AutoSize = true,
            Checked = settings.AutostartEnabled,
        };

        _pages = [BuildWelcome(), BuildConnect(), BuildPreferences()];

        _host = new Panel
        {
            Location = new Point(20, 18),
            Size = new Size(ClientSize.Width - 40, ClientSize.Height - 84),
        };
        Controls.Add(_host);

        _skipLink = new LinkLabel
        {
            Text = "Skip for now",
            AutoSize = true,
            Location = new Point(20, ClientSize.Height - 46),
        };
        _skipLink.LinkClicked += (_, _) => Finish();
        Controls.Add(_skipLink);

        _nextButton = new Button
        {
            Text = "Next",
            AutoSize = true,
            Location = new Point(ClientSize.Width - 110, ClientSize.Height - 50),
        };
        _nextButton.Click += (_, _) => Advance();
        Controls.Add(_nextButton);

        _backButton = new Button
        {
            Text = "Back",
            AutoSize = true,
            Location = new Point(ClientSize.Width - 196, ClientSize.Height - 50),
        };
        _backButton.Click += (_, _) => GoBack();
        Controls.Add(_backButton);

        // Subscribed here rather than where the panel is built: the handler
        // touches _nextButton, which does not exist until now.
        _signInPanel.SignedIn += _ =>
        {
            SignedIn = true;
            SignInCompleted?.Invoke();   // refresh the tray while they read
            Sync();
            // Next is the only thing left to do; make that obvious rather than
            // leaving the user hunting for it.
            _nextButton.Focus();
        };

        AcceptButton = _nextButton;
        ShowStep(0);
    }

    private Control BuildWelcome()
    {
        var page = new Panel { Dock = DockStyle.Fill };

        // Rendered from IconRenderer rather than shipped as an asset: it is the
        // literal thing the user is about to see in their tray.
        var sample = new PictureBox
        {
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = IconRenderer.Render("42", Band.Green).ToBitmap(),
            Location = new Point(0, 4),
        };
        page.Controls.Add(sample);

        page.Controls.Add(new Label
        {
            Text = "Your Claude usage, always visible",
            Font = new Font("Segoe UI Semibold", 12f),
            AutoSize = true,
            Location = new Point(62, 6),
        });

        page.Controls.Add(new Label
        {
            Text = "That icon sits in your notification area showing how much of your " +
                   "five-hour session you have used. It turns amber, then red, as you " +
                   "approach your limit. Click it for the weekly and per-model windows " +
                   "and a countdown to each reset.",
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Location = new Point(62, 34),
            ForeColor = SystemColors.GrayText,
        });

        page.Controls.Add(new Label
        {
            Text = "What it reads, and what it doesn't",
            Font = new Font("Segoe UI Semibold", 9.75f),
            AutoSize = true,
            Location = new Point(0, 150),
        });

        page.Controls.Add(new Label
        {
            Text =
                "ClaudeCounter reads your plan usage from Anthropic and nothing else. " +
                "Your sign-in is stored encrypted on this PC, for your Windows account " +
                "only, and is never sent anywhere except Anthropic's own servers.\r\n\r\n" +
                "There is no telemetry and no analytics.\r\n\r\n" +
                "ClaudeCounter is not affiliated with, endorsed by, or supported by Anthropic.",
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Location = new Point(0, 174),
            ForeColor = SystemColors.GrayText,
        });

        return page;
    }

    private Control BuildConnect()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        _signInPanel.Location = new Point(0, 0);
        page.Controls.Add(_signInPanel);
        return page;
    }

    private Control BuildPreferences()
    {
        var page = new Panel { Dock = DockStyle.Fill };

        page.Controls.Add(new Label
        {
            Text = "A couple of preferences",
            Font = new Font("Segoe UI Semibold", 12f),
            AutoSize = true,
            Location = new Point(0, 4),
        });

        page.Controls.Add(new Label
        {
            Text = "You can change these any time from the tray menu.",
            AutoSize = true,
            Location = new Point(0, 34),
            ForeColor = SystemColors.GrayText,
        });

        page.Controls.Add(new Label
        {
            Text = "Check my usage every",
            AutoSize = true,
            Location = new Point(0, 82),
        });
        _intervalCombo.Location = new Point(170, 79);
        page.Controls.Add(_intervalCombo);

        page.Controls.Add(new Label
        {
            Text = "Anything under 3 minutes may be rate limited; ClaudeCounter backs off " +
                   "automatically when that happens.",
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Location = new Point(0, 110),
            ForeColor = SystemColors.GrayText,
        });

        _autostartCheck.Location = new Point(0, 158);
        page.Controls.Add(_autostartCheck);

        return page;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        _host.Controls.Clear();
        _host.Controls.Add(_pages[_step]);
        Sync();
    }

    private void Advance()
    {
        if (_step < Steps - 1)
            ShowStep(_step + 1);
        else
            Finish();
    }

    private void GoBack() => ShowStep(_step - 1);

    private void Sync()
    {
        _backButton.Enabled = _step > 0;
        _nextButton.Text = _step == Steps - 1 ? "Finish" : "Next";

        // Sign-in is genuinely optional - a user with Claude Code installed
        // already sees their usage - so Next is never blocked, and the skip
        // link stays available throughout.
        _skipLink.Visible = _step < Steps - 1;
        _skipLink.Text = _step == 1 && !SignedIn ? "Skip for now" : "Skip setup";
    }

    private void Finish()
    {
        _settings.PollIntervalMinutes = AppSettings.IntervalPresets[_intervalCombo.SelectedIndex];
        _settings.AutostartEnabled = _autostartCheck.Checked;
        _settings.OnboardingCompleted = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing with the X still counts as done; nobody wants this window
        // reappearing every launch because they dismissed it.
        _settings.OnboardingCompleted = true;
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }
}
