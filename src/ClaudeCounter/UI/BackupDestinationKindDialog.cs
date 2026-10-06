namespace ClaudeCounter.UI;

/// <summary>
/// S17c: the destinations dialog's "Add..." first step - pick which kind of
/// destination to add (GitHub / Google Drive / OneDrive / Dropbox / NAS /
/// rclone remote). A thin ListBox shell, mirroring SyncFolderDetectDialog's
/// "ListBox + Use/Cancel" shape closely. Several destinations of the same
/// kind are explicitly allowed - this dialog is offered every time "Add..."
/// is clicked, with no memory of what was picked last time. Never construct
/// this (or any Form) from a test except via SettingsFormSmokeTests' STA
/// helper.
/// </summary>
public sealed class BackupDestinationKindDialog : Form
{
    private const int DialogWidth = 380;
    private const int Pad = 16;
    private const int BottomBarHeight = 52;
    private const int ListHeight = 150;

    private readonly ListBox _list;

    /// <summary>The chosen option - only set once ShowDialog() has returned DialogResult.OK.</summary>
    public BackupDestinationNaming.KindOption? Selected { get; private set; }

    public BackupDestinationKindDialog(Palette palette)
    {
        Text = "Add a Backup Destination";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        KeyPreview = true;

        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        var label = new Label
        {
            Text = "What kind of destination do you want to add?",
            AutoSize = true,
            MaximumSize = new Size(fullWidth, 0),
            Font = Font,
            ForeColor = palette.Fore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, y),
        };
        Controls.Add(label);
        y += label.PreferredHeight + 8;

        _list = new ListBox
        {
            Location = new Point(Pad, y),
            Size = new Size(fullWidth, ListHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var option in BackupDestinationNaming.KindOptions)
            _list.Items.Add(option.Label);
        _list.SelectedIndex = 0;
        Controls.Add(_list);
        y += _list.Height + 8;

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });

        var cancelButton = NewDialogButton("Cancel", palette);
        cancelButton.DialogResult = DialogResult.Cancel;

        var addButton = NewDialogButton("Add...", palette);
        addButton.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0)
                return;
            Selected = BackupDestinationNaming.KindOptions[_list.SelectedIndex];
            DialogResult = DialogResult.OK;
            Close();
        };
        _list.DoubleClick += (_, _) => addButton.PerformClick();

        var cancelX = DialogWidth - Pad - cancelButton.Width;
        var addX = cancelX - 8 - addButton.Width;
        var btnY = (BottomBarHeight - addButton.Height) / 2;
        cancelButton.Location = new Point(cancelX, btnY);
        addButton.Location = new Point(addX, btnY);
        bottomBar.Controls.Add(cancelButton);
        bottomBar.Controls.Add(addButton);
        Controls.Add(bottomBar);

        AcceptButton = addButton;
        CancelButton = cancelButton;

        ClientSize = new Size(DialogWidth, y + Pad + BottomBarHeight);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
    }

    private static Button NewDialogButton(string text, Palette palette)
    {
        var button = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Padding = new Padding(10, 4, 10, 4),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        button.FlatAppearance.BorderColor = palette.Border;
        button.Size = button.GetPreferredSize(Size.Empty);
        return button;
    }
}
