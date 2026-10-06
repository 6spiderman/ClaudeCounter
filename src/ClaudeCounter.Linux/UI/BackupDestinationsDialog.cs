using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// Settings -> Backup -> "Manage destinations...": add, edit and remove
/// destinations. Linux port of the Windows dialog - each change is saved to
/// backup.json straight away, exactly as there.
/// </summary>
public sealed class BackupDestinationsDialog : Window
{
    private readonly string _backupConfigPath;
    private readonly string _backupStatusPath;
    private readonly string _sourceRoot;
    private readonly BackupConfig _config;
    private readonly ListBox _list;
    private readonly Button _editButton;
    private readonly Button _removeButton;

    /// <summary>True once anything was added, edited or removed.</summary>
    public bool Changed { get; private set; }

    public BackupDestinationsDialog(string backupConfigPath, string backupStatusPath)
    {
        _backupConfigPath = backupConfigPath;
        _backupStatusPath = backupStatusPath;
        _config = BackupConfig.Load(backupConfigPath);
        _sourceRoot = _config.SourceRoot;

        Title = "Manage Backup Destinations";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox { Height = 220 };
        _list.SelectionChanged += (_, _) => RefreshButtonsEnabled();
        _list.DoubleTapped += async (_, _) => { if (_editButton!.IsEnabled) await OnEditAsync(); };

        var add = new Button { Content = "Add..." };
        add.Click += async (_, _) => await OnAddAsync();
        _editButton = new Button { Content = "Edit..." };
        _editButton.Click += async (_, _) => await OnEditAsync();
        _removeButton = new Button { Content = "Remove..." };
        _removeButton.Click += async (_, _) => await OnRemoveAsync();

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Add, edit, or remove any combination of destinations - GitHub, Google Drive, " +
                           "OneDrive, Dropbox, NAS / network share, or an rclone remote. Several of the same " +
                           "kind are allowed.",
                    TextWrapping = TextWrapping.Wrap,
                },
                DestinationTable.Header(),
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, _editButton, _removeButton } },
                close,
            },
        };

        RefreshList();
    }

    private BackupDestination? Selected =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _config.Destinations.Count
            ? _config.Destinations[_list.SelectedIndex]
            : null;

    private void RefreshButtonsEnabled()
    {
        var hasSelection = Selected is not null;
        _editButton.IsEnabled = hasSelection;
        _removeButton.IsEnabled = hasSelection;
    }

    private void RefreshList()
    {
        var selectedId = Selected?.Id;
        var status = BackupStatus.Load(_backupStatusPath);
        _list.ItemsSource = _config.Destinations.Select(d => DestinationTable.Row(d, status)).ToList();
        _list.SelectedIndex = _config.Destinations.FindIndex(d => d.Id == selectedId);
        RefreshButtonsEnabled();
    }

    private async Task OnAddAsync()
    {
        var kindDialog = new BackupDestinationKindDialog();
        if (!await kindDialog.ShowDialog<bool>(this) || kindDialog.Selected is not { } option)
            return;

        var baseName = BackupDestinationNaming.KindDisplayName(option.Kind, option.Provider);
        var seed = new BackupDestination
        {
            Id = BackupDestination.NewId(),
            Name = BackupDestinationNaming.GenerateUniqueName(baseName, _config.Destinations.Select(d => d.Name)),
            Kind = option.Kind,
            SyncProvider = option.Provider,
        };

        var editDialog = new BackupDestinationEditDialog(option.Kind, option.Provider, _sourceRoot, seed, isNew: true);
        if (!await editDialog.ShowDialog<bool>(this))
            return;

        _config.Destinations.Add(BuildDestination(seed.Id, option.Kind, option.Provider, editDialog));
        Persist();
        RefreshList();
        _list.SelectedIndex = _config.Destinations.Count - 1;
    }

    private async Task OnEditAsync()
    {
        if (Selected is not { } existing)
            return;
        var editDialog = new BackupDestinationEditDialog(existing.Kind, existing.SyncProvider, _sourceRoot, existing, isNew: false);
        if (!await editDialog.ShowDialog<bool>(this))
            return;

        var index = _config.Destinations.FindIndex(d => d.Id == existing.Id);
        if (index >= 0)
            _config.Destinations[index] = BuildDestination(existing.Id, existing.Kind, existing.SyncProvider, editDialog);
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

    private async Task OnRemoveAsync()
    {
        if (Selected is not { } destination)
            return;
        var confirmed = await MessageDialog.ConfirmAsync(this,
            $"Remove '{destination.Name}' from ClaudeCounter's backup configuration?\n\n" +
            "This only removes it from ClaudeCounter's local settings - nothing is deleted at " +
            $"{destination.Name} itself. Any backups already there are left exactly as they are.",
            "Remove", "Cancel");
        if (!confirmed)
            return;

        _config.Destinations.RemoveAll(d => d.Id == destination.Id);
        Persist();
        BackupStatus.RemoveDestination(_backupStatusPath, destination.Id);
        RefreshList();
    }

    private void Persist()
    {
        _config.Save(_backupConfigPath);
        Changed = true;
    }
}

/// <summary>
/// The Name / Kind / Enabled / Last run table shared by the destinations
/// dialog and the summary on the Backup tab.
/// </summary>
public static class DestinationTable
{
    // Proportional, so the same table fits the destinations dialog and the
    // narrower Backup tab.
    private const string Columns = "1.6*,1.4*,0.7*,1.6*";

    public static Control Header() => Row("Name", "Kind", "Enabled", "Last run", header: true);

    public static Control Row(BackupDestination destination, BackupStatus status) => Row(
        destination.Name,
        BackupDestinationNaming.KindDisplayName(destination.Kind, destination.SyncProvider),
        destination.Enabled ? "Yes" : "No",
        LastRunSummary(status.For(destination.Id)),
        header: false);

    /// <summary>Same wording as the Windows summary.</summary>
    public static string LastRunSummary(DestinationStatus status)
    {
        if (status.LastAttemptUtc is null)
            return "Never run";
        if (status.LastOutcome == BackupOutcome.Failed)
            return "Failed";
        return status.LastSuccessUtc is { } last
            ? $"OK - {TimeText.Ago(last, DateTimeOffset.UtcNow)}"
            : "Never run";
    }

    private static Control Row(string name, string kind, string enabled, string lastRun, bool header)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Columns),
            // ListBox items get their own padding; line the header up with them.
            Margin = header ? new Thickness(12, 0, 12, 0) : default,
        };
        var cells = new[] { name, kind, enabled, lastRun };
        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }
}
