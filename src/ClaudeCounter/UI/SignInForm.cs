using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.UI;

/// <summary>Modal host for <see cref="SignInPanel"/>, opened from the tray menu.</summary>
public sealed class SignInForm : Form
{
    private readonly SignInPanel _panel;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _autoClose;

    public OAuthSession? Session { get; private set; }

    public SignInForm(SignInCoordinator coordinator)
    {
        Text = "Sign in to Claude";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 340);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        _closeButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(0),
        };

        // Docked rather than absolutely positioned: the button is AutoSize, so
        // a Location computed from PreferredSize at construction is stale by
        // the time it renders and the button ends up clipped.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 8, 18, 14),
        };
        buttons.Controls.Add(_closeButton);

        _panel = new SignInPanel(coordinator)
        {
            Location = new Point(18, 16),
        };
        _panel.SignedIn += OnSignedIn;

        Controls.Add(_panel);
        Controls.Add(buttons);
        CancelButton = _closeButton;
        AcceptButton = _panel.DefaultButton;

        // Give the success message a moment to register before the dialog
        // vanishes; closing instantly reads as "did that work?".
        _autoClose = new System.Windows.Forms.Timer { Interval = 900 };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose.Stop();
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private void OnSignedIn(OAuthSession session)
    {
        Session = session;
        _closeButton.Text = "Done";
        _autoClose.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // A tray app has no owner window, so without this the dialog can appear
        // behind whatever the user was working in and look like nothing happened.
        TopMost = true;
        Activate();
        TopMost = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _autoClose.Dispose();
        base.Dispose(disposing);
    }
}
