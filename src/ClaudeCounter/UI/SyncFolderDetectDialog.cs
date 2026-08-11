using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// The Backup tab's "Detect..." dialog for the sync-folder transport: lists
/// every <see cref="SyncFolderCandidate"/> <see cref="SyncFolderScanner.Detect"/>
/// found on this machine (an OneDrive/Google Drive/Dropbox folder, or a NAS
/// share's UNC path) and lets the user pick one rather than typing a path by
/// hand - the biggest usability win of the sync-folder transport (see
/// BackupHelpText.SyncFolder). A thin, largely non-branching shell around a
/// ListBox, like BackupHelpDialog and BackupAdvancedDialog - the caller
/// (SettingsForm) reads <see cref="SelectedPath"/> after ShowDialog()
/// returns DialogResult.OK. Never construct this (or any Form) from a test
/// except via SettingsFormSmokeTests' dedicated STA helper.
/// </summary>
public sealed class SyncFolderDetectDialog : Form
{
    private const int DialogWidth = 460;
    private const int Pad = 16;
    private const int BottomBarHeight = 52;
    private const int ListHeight = 160;

    private readonly IReadOnlyList<SyncFolderCandidate> _candidates;
    private readonly ListBox _list;

    /// <summary>The chosen candidate's Path - only set once ShowDialog() has returned DialogResult.OK.</summary>
    public string? SelectedPath { get; private set; }

    public SyncFolderDetectDialog(Palette palette, IReadOnlyList<SyncFolderCandidate> candidates)
    {
        _candidates = candidates;

        Text = "Detected Sync Folders";
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
            Text = candidates.Count > 0
                ? "Pick a folder your sync client or NAS already watches:"
                : "No sync folder was found automatically on this machine. " +
                  "Use Browse... instead, or type the path directly.",
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
        foreach (var candidate in candidates)
            _list.Items.Add($"{candidate.DisplayName}: {candidate.Path}");
        if (_list.Items.Count > 0)
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

        var useButton = NewDialogButton("Use this folder", palette);
        useButton.Enabled = _list.Items.Count > 0;
        useButton.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0)
                return;
            SelectedPath = _candidates[_list.SelectedIndex].Path;
            DialogResult = DialogResult.OK;
            Close();
        };
        // Double-clicking a candidate is the same as selecting it and
        // clicking "Use this folder" - a shortcut, not a second code path:
        // it just invokes the same handler.
        _list.DoubleClick += (_, _) => useButton.PerformClick();

        var cancelX = DialogWidth - Pad - cancelButton.Width;
        var useX = cancelX - 8 - useButton.Width;
        var btnY = (BottomBarHeight - useButton.Height) / 2;
        cancelButton.Location = new Point(cancelX, btnY);
        useButton.Location = new Point(useX, btnY);
        bottomBar.Controls.Add(cancelButton);
        bottomBar.Controls.Add(useButton);
        Controls.Add(bottomBar);

        AcceptButton = useButton;
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
