using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// S17c: "Manage destinations..." - the dialog that replaces the old S16
/// "Back up to" selector's hard "GitHub plus one cloud/NAS destination"
/// limit. Lists every configured <see cref="BackupDestination"/> (name,
/// kind, enabled state, last-run health) and lets the user Add, Edit, or
/// Remove any of them, in any combination - including several of the same
/// kind. Reachable from a single button on the Backup tab (see
/// SettingsForm.BuildBackupPage), which is what let the tab itself shrink
/// back under its display budget once the GitHub/Drive blocks moved here.
///
/// Every mutating action (Add/Edit/Remove) saves IMMEDIATELY to <see
/// cref="_backupConfigPath"/> - it does not wait for this dialog's own Close
/// or the outer SettingsForm's OK, mirroring the Backup tab's own existing
/// "Save and register schedule" button, which has always persisted
/// independently of the main dialog's OK/Cancel. <see cref="Changed"/> lets
/// the caller (SettingsForm) know whether anything actually happened, so it
/// can refresh its own read-only summary and tell the tray to re-evaluate
/// backup health promptly (see SettingsForm.BackupDestinationsChanged) -
/// without a prompt refresh, a removed destination's badge would linger
/// until the next scheduled poll.
///
/// Never construct this (or any Form) from a test except via
/// SettingsFormSmokeTests' dedicated STA helper.
/// </summary>
public sealed class BackupDestinationsDialog : Form
{
    private const int DialogWidth = 620;
    private const int Pad = 16;
    private const int BottomBarHeight = 52;
    private const int RowGap = 8;
    private const int ListHeight = 220;

    private readonly Palette _palette;
    private readonly string _backupConfigPath;
    private readonly string _backupStatusPath;
    private readonly string _sourceRoot;

    private BackupConfig _config;
    private ListView _list = null!;
    private Button _editButton = null!;
    private Button _removeButton = null!;

    /// <summary>True once at least one Add/Edit/Remove has actually persisted - see this class's own doc comment.</summary>
    public bool Changed { get; private set; }

    public BackupDestinationsDialog(Palette palette, string backupConfigPath, string backupStatusPath)
    {
        _palette = palette;
        _backupConfigPath = backupConfigPath;
        _backupStatusPath = backupStatusPath;
        _config = BackupConfig.Load(backupConfigPath);
        _sourceRoot = _config.SourceRoot;

        Text = "Manage Backup Destinations";
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

        var intro = new Label
        {
            Text = "Add, edit, or remove any combination of destinations - GitHub, Google Drive, " +
                   "OneDrive, Dropbox, NAS / network share, or an rclone remote. Several of the same " +
                   "kind are allowed.",
            AutoSize = true,
            MaximumSize = new Size(fullWidth, 0),
            Font = Font,
            ForeColor = palette.SubtleFore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, y),
        };
        Controls.Add(intro);
        y += intro.PreferredHeight + RowGap;

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Location = new Point(Pad, y),
            Size = new Size(fullWidth, ListHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _list.Columns.Add("Name", 170);
        _list.Columns.Add("Kind", 150);
        _list.Columns.Add("Enabled", 70);
        _list.Columns.Add("Last run", fullWidth - 170 - 150 - 70);
        _list.SelectedIndexChanged += (_, _) => RefreshButtonsEnabled();
        _list.DoubleClick += (_, _) => { if (_editButton.Enabled) OnEdit(); };
        Controls.Add(_list);
        y += _list.Height + RowGap;

        var addButton = NewFlatButton("Add...", palette);
        addButton.Click += (_, _) => OnAdd();

        _editButton = NewFlatButton("Edit...", palette);
        _editButton.Click += (_, _) => OnEdit();

        _removeButton = NewFlatButton("Remove...", palette);
        _removeButton.Click += (_, _) => OnRemove();

        addButton.Location = new Point(Pad, y);
        _editButton.Location = new Point(addButton.Right + 8, y);
        _removeButton.Location = new Point(_editButton.Right + 8, y);
        Controls.Add(addButton);
        Controls.Add(_editButton);
        Controls.Add(_removeButton);
        y += addButton.Height + RowGap;

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(DialogWidth, 1), BackColor = palette.Border });
        var closeButton = NewDialogButton("Close", palette);
        closeButton.DialogResult = DialogResult.OK;
        closeButton.Location = new Point(DialogWidth - Pad - closeButton.Width, (BottomBarHeight - closeButton.Height) / 2);
        bottomBar.Controls.Add(closeButton);
        Controls.Add(bottomBar);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        ClientSize = new Size(DialogWidth, y + Pad + BottomBarHeight);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        RefreshList();
    }

    private BackupDestination? Selected =>
        _list.SelectedItems.Count > 0 ? (BackupDestination)_list.SelectedItems[0].Tag! : null;

    private void RefreshButtonsEnabled()
    {
        var hasSelection = Selected is not null;
        _editButton.Enabled = hasSelection;
        _removeButton.Enabled = hasSelection;
    }

    private void RefreshList()
    {
        var selectedId = Selected?.Id;
        var status = BackupStatus.Load(_backupStatusPath);

        _list.Items.Clear();
        foreach (var destination in _config.Destinations)
        {
            var item = new ListViewItem(new[]
            {
                destination.Name,
                BackupDestinationNaming.KindDisplayName(destination.Kind, destination.SyncProvider),
                destination.Enabled ? "Yes" : "No",
                LastRunSummary(status.For(destination.Id)),
            })
            { Tag = destination };
            _list.Items.Add(item);
            if (destination.Id == selectedId)
                item.Selected = true;
        }

        RefreshButtonsEnabled();
    }

    private static string LastRunSummary(DestinationStatus status)
    {
        if (status.LastAttemptUtc is null)
            return "Never run";
        if (status.LastOutcome == BackupOutcome.Failed)
            return "Failed";
        return status.LastSuccessUtc is { } last
            ? $"OK - {TimeText.Ago(last, DateTimeOffset.UtcNow)}"
            : "Never run";
    }

    private void OnAdd()
    {
        using var kindDialog = new BackupDestinationKindDialog(_palette);
        if (kindDialog.ShowDialog(this) != DialogResult.OK || kindDialog.Selected is not { } option)
            return;

        var baseName = BackupDestinationNaming.KindDisplayName(option.Kind, option.Provider);
        var seed = new BackupDestination
        {
            Id = BackupDestination.NewId(),
            Name = BackupDestinationNaming.GenerateUniqueName(baseName, _config.Destinations.Select(d => d.Name)),
            Kind = option.Kind,
            SyncProvider = option.Provider,
        };

        using var editDialog = new BackupDestinationEditDialog(_palette, option.Kind, option.Provider, _sourceRoot, seed, isNew: true);
        if (editDialog.ShowDialog(this) != DialogResult.OK)
            return;

        var destination = BuildDestination(seed.Id, option.Kind, option.Provider, editDialog);
        _config.Destinations.Add(destination);
        Persist();
        RefreshList();
    }

    private void OnEdit()
    {
        if (Selected is not { } existing)
            return;

        using var editDialog = new BackupDestinationEditDialog(
            _palette, existing.Kind, existing.SyncProvider, _sourceRoot, existing, isNew: false);
        if (editDialog.ShowDialog(this) != DialogResult.OK)
            return;

        var updated = BuildDestination(existing.Id, existing.Kind, existing.SyncProvider, editDialog);
        var index = _config.Destinations.FindIndex(d => d.Id == existing.Id);
        if (index >= 0)
            _config.Destinations[index] = updated;
        Persist();
        RefreshList();
    }

    private static BackupDestination BuildDestination(
        string id, DestinationKind kind, SyncProvider syncProvider, BackupDestinationEditDialog dialog) => new()
    {
        Id = id,
        Name = dialog.DestinationName,
        Kind = kind,
        SyncProvider = syncProvider,
        Enabled = dialog.DestinationEnabled,
        RemoteUrl = dialog.RemoteUrl,
        Branch = dialog.Branch,
        FolderPath = dialog.FolderPath,
        RcloneRemote = dialog.RcloneRemote,
        Include = dialog.Include,
        Exclude = dialog.Exclude,
        KeepLastCount = dialog.KeepLastCount,
        DeleteOlderThanDays = dialog.DeleteOlderThanDays,
    };

    /// <summary>
    /// Removal is explicit user action against durable state - per S17a's
    /// own doc comment on BackupStatus.RemoveDestination, this is
    /// deliberately NOT routed through the swallow-on-failure pattern
    /// BackupStatusWriter uses for a backup RUN. A write failure here
    /// surfaces to the user via the exception escaping to whatever
    /// unhandled-exception handling this app already has for a UI action,
    /// the same as any other Save() call in SettingsForm.
    /// </summary>
    private void OnRemove()
    {
        if (Selected is not { } destination)
            return;

        var confirmed = MessageBox.Show(this,
            $"Remove '{destination.Name}' from ClaudeCounter's backup configuration?\n\n" +
            "This only removes it from ClaudeCounter's local settings - nothing is deleted at " +
            $"{destination.Name} itself. Any backups already there are left exactly as they are.",
            "Remove backup destination", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirmed != DialogResult.Yes)
            return;

        _config.Destinations.RemoveAll(d => d.Id == destination.Id);
        Persist();
        // S17a: drops the status entry too, so BackupHealth stops reporting
        // a destination that no longer exists - see BackupStatus.
        // WithoutDestination's own doc comment. Combined with SettingsForm
        // telling the tray to re-evaluate promptly on Changed (see this
        // class's own doc comment), this is what actually clears a lingering
        // badge/notification instead of merely stopping future ones.
        BackupStatus.RemoveDestination(_backupStatusPath, destination.Id);
        RefreshList();
    }

    private void Persist()
    {
        _config.Save(_backupConfigPath);
        Changed = true;
    }

    private static Button NewFlatButton(string text, Palette palette)
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

    private static Button NewDialogButton(string text, Palette palette)
    {
        var button = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Size = new Size(84, 28),
        };
        button.FlatAppearance.BorderColor = palette.Border;
        return button;
    }
}
