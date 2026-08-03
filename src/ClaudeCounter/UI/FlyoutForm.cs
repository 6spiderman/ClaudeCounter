using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public sealed class FlyoutForm : Form
{
    private const int FlyoutWidth = 320;
    private const int Pad = 14;

    private readonly Func<AppSettings> _getSettings;
    private readonly System.Windows.Forms.Timer _countdownTimer;
    private PollState? _state;
    private string? _updateVersion;

    /// <summary>Used by the tray click handler to ignore the click that just closed us.</summary>
    public DateTime HiddenAtUtc { get; private set; } = DateTime.MinValue;

    public event Action? RefreshRequested;
    public event Action? UpdateRequested;
    public event Action? SignInRequested;

    public FlyoutForm(Func<AppSettings> getSettings)
    {
        _getSettings = getSettings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Width = FlyoutWidth;
        Font = new Font("Segoe UI", 9f);

        _countdownTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _countdownTimer.Tick += (_, _) => RebuildContent();

        Deactivate += (_, _) =>
        {
            HiddenAtUtc = DateTime.UtcNow;
            Hide();
            _countdownTimer.Stop();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryRoundCorners(Handle);
    }

    public void UpdateState(PollState state)
    {
        _state = state;
        if (Visible)
            RebuildContent();
    }

    /// <summary>Adds a one-line "a newer release exists" notice to the flyout.</summary>
    public void ShowUpdateAvailable(string version)
    {
        _updateVersion = version;
        if (Visible)
            RebuildContent();
    }

    public void ShowNear(Point anchor)
    {
        RebuildContent();
        var workingArea = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Max(workingArea.Left + 8,
            Math.Min(anchor.X - Width / 2, workingArea.Right - Width - 8));
        var y = Math.Max(workingArea.Top + 8,
            Math.Min(anchor.Y - Height - 12, workingArea.Bottom - Height - 8));
        Location = new Point(x, y);
        Show();
        Activate();
        _countdownTimer.Start();
    }

    private void RebuildContent()
    {
        var palette = Theme.Current();
        var settings = _getSettings();
        BackColor = palette.Back;

        SuspendLayout();
        Controls.Clear();

        var y = Pad;
        y = AddLabel("Claude Max usage", new Font("Segoe UI Semibold", 10.5f), palette.Fore, y);
        y += 6;

        var snapshot = _state?.Snapshot;
        if (snapshot is not null)
        {
            var now = DateTimeOffset.UtcNow;
            y = AddUsageRow("5-hour session", snapshot.FiveHour, settings, palette, now, y);
            y = AddUsageRow("Weekly (all models)", snapshot.SevenDay, settings, palette, now, y);
            y = AddUsageRow("Weekly (Opus)", snapshot.SevenDayOpus, settings, palette, now, y);
            y = AddUsageRow("Weekly (Sonnet)", snapshot.SevenDaySonnet, settings, palette, now, y);

            if (snapshot.ExtraUsage is { IsEnabled: true } extra)
            {
                var used = extra.UsedCredits?.ToString("0.00") ?? "?";
                var limit = extra.MonthlyLimit?.ToString("0.##") ?? "?";
                var pct = extra.Utilization is { } u ? $" ({u:0}%)" : "";
                y = AddLabel($"Extra usage: ${used} / ${limit}{pct}",
                    Font, palette.Fore, y);
                y += 4;
            }
        }
        else
        {
            y = AddLabel("No data yet", Font, palette.SubtleFore, y);
            y += 4;
        }

        var needsSignIn = _state?.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;

        if (_state is { Problem: not ProblemKind.None, ProblemMessage: { } problem })
        {
            var color = needsSignIn ? Theme.BandColor(Band.Amber) : Theme.BandColor(Band.Red);
            y = AddLabel(problem, Font, color, y, wrap: true);
            y += 4;
        }
        else if (_state is { IsBootstrapped: true })
        {
            // Working, but on Claude Code's session. Nudge without alarming:
            // this state is functional, it just cannot refresh itself.
            y = AddLabel("Using Claude Code's session - sign in for your own.",
                new Font("Segoe UI", 8.5f), Theme.BandColor(Band.Amber), y, wrap: true);
            y += 4;
        }

        if (_updateVersion is { } newVersion)
        {
            var updateLink = new LinkLabel
            {
                Text = $"ClaudeCounter v{newVersion} is available",
                BackColor = Color.Transparent,
                LinkColor = Theme.BandColor(Band.Green),
                ActiveLinkColor = Theme.BandColor(Band.Green),
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = true,
                Location = new Point(Pad, y),
            };
            updateLink.LinkClicked += (_, _) => UpdateRequested?.Invoke();
            Controls.Add(updateLink);
            y += updateLink.PreferredHeight + 6;
        }

        y += 6;
        var updated = _state?.LastSuccessAt is { } last
            ? $"Updated {last.ToLocalTime():HH:mm}"
            : "Never updated";
        var updatedLabel = new Label
        {
            Text = updated,
            ForeColor = palette.SubtleFore,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(Pad, y + 5),
        };
        Controls.Add(updatedLabel);

        var refreshButton = MakeButton("Refresh", palette, Width - Pad - 80, y);
        refreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        Controls.Add(refreshButton);

        // Offered whenever signing in would help, including on the bootstrap
        // path where things technically work but cannot renew themselves.
        if (needsSignIn || _state is { IsBootstrapped: true })
        {
            var signInButton = MakeButton("Sign in", palette, Width - Pad - 80 - 8 - 80, y);
            signInButton.Click += (_, _) => SignInRequested?.Invoke();
            Controls.Add(signInButton);
        }

        Height = y + 28 + Pad;
        ResumeLayout();
    }

    private static Button MakeButton(string text, Palette palette, int x, int y)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = palette.Fore,
            BackColor = palette.BarBack,
            Size = new Size(80, 28),
            Location = new Point(x, y),
        };
        button.FlatAppearance.BorderColor = palette.Border;
        return button;
    }

    private int AddUsageRow(
        string label, UsageWindow? window, AppSettings settings, Palette palette,
        DateTimeOffset now, int y)
    {
        if (window is null)
            return y;

        var band = IconRenderer.BandFor(window.Utilization, settings);

        var nameLabel = new Label
        {
            Text = label,
            ForeColor = palette.Fore,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(Pad, y),
        };
        Controls.Add(nameLabel);

        var percentLabel = new Label
        {
            Text = $"{window.Utilization:0}%",
            ForeColor = Theme.BandColor(band),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 9f),
            AutoSize = true,
            TextAlign = ContentAlignment.TopRight,
        };
        Controls.Add(percentLabel);
        percentLabel.Location = new Point(Width - Pad - percentLabel.PreferredWidth, y);

        y += 20;
        var bar = new BandBar
        {
            Value = window.Utilization,
            BarColor = Theme.BandColor(band),
            TrackColor = palette.BarBack,
            Location = new Point(Pad, y),
            Size = new Size(Width - Pad * 2, 6),
        };
        Controls.Add(bar);

        y += 10;
        var resetLabel = new Label
        {
            Text = window.ResetsAt is { } resetsAt
                ? $"Resets in {TimeText.Countdown(resetsAt, now)}"
                : "No active limit",
            ForeColor = palette.SubtleFore,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8f),
            AutoSize = true,
            Location = new Point(Pad, y),
        };
        Controls.Add(resetLabel);

        return y + 24;
    }

    private int AddLabel(string text, Font font, Color color, int y, bool wrap = false)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = Color.Transparent,
            Location = new Point(Pad, y),
        };
        if (wrap)
        {
            label.MaximumSize = new Size(Width - Pad * 2, 0);
            label.AutoSize = true;
        }
        else
        {
            label.AutoSize = true;
        }
        Controls.Add(label);
        return y + label.PreferredHeight + 2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _countdownTimer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class BandBar : Control
    {
        public double Value { get; set; }
        public Color BarColor { get; set; }
        public Color TrackColor { get; set; }

        public BandBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var track = new SolidBrush(TrackColor);
            e.Graphics.FillRectangle(track, ClientRectangle);
            var width = (int)(ClientRectangle.Width * Math.Clamp(Value, 0, 100) / 100.0);
            if (width > 0)
            {
                using var bar = new SolidBrush(BarColor);
                e.Graphics.FillRectangle(bar, new Rectangle(0, 0, width, ClientRectangle.Height));
            }
        }
    }
}
