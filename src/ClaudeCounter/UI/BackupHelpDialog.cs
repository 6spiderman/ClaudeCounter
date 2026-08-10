namespace ClaudeCounter.UI;

/// <summary>
/// The fuller Backup guide, opened from the "Help" button on the Backup tab.
/// Scrollable (the guide is long), themed via Palette, dismissible with Esc
/// or the Close button. Not unit tested - it is a thin, non-branching shell
/// around BackupHelpText.FullGuide, which is what carries the tested facts.
/// Never construct this (or any Form) from a test.
/// </summary>
public sealed class BackupHelpDialog : Form
{
    private const int DialogWidth = 480;
    private const int DialogHeight = 520;
    private const int BottomBarHeight = 48;
    private const int Pad = 16;

    public BackupHelpDialog(Palette palette)
    {
        Text = "Backup Help";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        ClientSize = new Size(DialogWidth, DialogHeight);
        KeyPreview = true;

        var closeButton = new Button
        {
            Text = "Close",
            Font = Font,
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Size = new Size(84, 28),
            DialogResult = DialogResult.Cancel,
        };
        closeButton.FlatAppearance.BorderColor = palette.Border;
        closeButton.Location = new Point(DialogWidth - Pad - closeButton.Width, (BottomBarHeight - closeButton.Height) / 2);
        closeButton.Click += (_, _) => Close();

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });
        bottomBar.Controls.Add(closeButton);

        // AutoScroll panel rather than a fixed-height label: the guide text
        // is long by design (it is the fuller explanation, not the short
        // per-field popup) and is expected to exceed the dialog's height at
        // most DPI settings - unlike SettingsForm's own pages, scrolling here
        // is intentional rather than the thing being avoided.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = palette.Back };
        var body = new Label
        {
            Text = BackupHelpText.FullGuide,
            AutoSize = true,
            MaximumSize = new Size(DialogWidth - Pad * 2 - SystemInformation.VerticalScrollBarWidth, 0),
            Font = Font,
            ForeColor = palette.Fore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, Pad),
        };
        scroll.Controls.Add(body);

        Controls.Add(scroll);
        Controls.Add(bottomBar);

        CancelButton = closeButton;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
    }
}
