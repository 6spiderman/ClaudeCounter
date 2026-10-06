using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// Add/Edit one backup destination - Linux port of the Windows
/// BackupDestinationEditDialog, same fields, wording and validation. The
/// caller builds the saved BackupDestination from the public properties.
/// </summary>
/// <remarks>
/// Sync-folder detection runs off the UI thread: probing a network mount
/// whose server is down can block for many seconds on Linux.
/// </remarks>
public sealed class BackupDestinationEditDialog : Window
{
    private readonly DestinationKind _kind;
    private readonly SyncProvider _syncProvider;
    private readonly string _sourceRoot;

    private readonly TextBox _nameBox;
    private readonly CheckBox _enabledCheck;
    private TextBox? _remoteUrlBox;
    private TextBox? _branchBox;
    private TextBox? _folderPathBox;
    private TextBox? _rcloneRemoteBox;
    private readonly TextBox _includeBox;
    private readonly TextBox _excludeBox;
    private CheckBox? _keepLastEnabled;
    private NumericUpDown? _keepLastCount;
    private CheckBox? _deleteOlderEnabled;
    private NumericUpDown? _deleteOlderDays;

    public string DestinationName => (_nameBox.Text ?? "").Trim();
    public bool DestinationEnabled => _enabledCheck.IsChecked == true;
    public string RemoteUrl => _remoteUrlBox?.Text?.Trim() ?? "";
    public string Branch => _branchBox?.Text?.Trim() is { Length: > 0 } b ? b : "main";
    public string FolderPath => _folderPathBox?.Text?.Trim() ?? "";
    public string RcloneRemote => _rcloneRemoteBox?.Text?.Trim() ?? "";
    public List<string> Include => SplitLines(_includeBox.Text);
    public List<string> Exclude => SplitLines(_excludeBox.Text);
    public int? KeepLastCount => _keepLastEnabled?.IsChecked == true ? (int)(_keepLastCount!.Value ?? 30) : null;
    public int? DeleteOlderThanDays => _deleteOlderEnabled?.IsChecked == true ? (int)(_deleteOlderDays!.Value ?? 90) : null;

    /// <summary>Show with <c>await dialog.ShowDialog&lt;bool&gt;(owner)</c>; true on OK.</summary>
    public BackupDestinationEditDialog(
        DestinationKind kind, SyncProvider syncProvider, string sourceRoot,
        BackupDestination seed, bool isNew)
    {
        _kind = kind;
        _syncProvider = syncProvider;
        _sourceRoot = sourceRoot;

        Title = isNew ? "Add Backup Destination" : "Edit Backup Destination";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = $"Kind: {BackupDestinationNaming.KindDisplayName(kind, syncProvider)}",
            FontWeight = FontWeight.SemiBold,
        });

        _nameBox = new TextBox { Text = seed.Name };
        panel.Children.Add(FieldRow("Name", _nameBox, BackupHelpText.Name));

        _enabledCheck = new CheckBox
        {
            Content = kind == DestinationKind.GitHub ? "Back up to this GitHub repo" : "Back up to this destination",
            IsChecked = seed.Enabled,
        };
        panel.Children.Add(WithInfo(_enabledCheck,
            kind == DestinationKind.GitHub ? BackupHelpText.GithubEnabled : BackupHelpText.DestinationEnabled));

        switch (kind)
        {
            case DestinationKind.GitHub:
                BuildGitHubFields(panel, seed);
                break;
            case DestinationKind.SyncFolder:
                BuildSyncFolderFields(panel, seed, isNew);
                break;
            default:
                BuildRcloneFields(panel, seed);
                break;
        }

        // Include, with the file picker.
        var choose = new Button { Content = "Choose files..." };
        choose.Click += async (_, _) => await OnChooseFilesAsync();
        panel.Children.Add(HeaderRow("Include (one pattern per line)", BackupHelpText.Include, choose));
        _includeBox = MultiLine(seed.Include);
        panel.Children.Add(_includeBox);

        panel.Children.Add(HeaderRow("Exclude (one pattern per line)", BackupHelpText.Exclude));
        _excludeBox = MultiLine(seed.Exclude);
        panel.Children.Add(_excludeBox);

        BuildRetentionSection(panel, kind, seed);

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += async (_, _) => await OnOkAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, ok },
        });

        Content = panel;
    }

    private void BuildGitHubFields(StackPanel panel, BackupDestination seed)
    {
        panel.Children.Add(HeaderRow("Remote URL", BackupHelpText.RemoteUrl));
        _remoteUrlBox = new TextBox { Text = seed.RemoteUrl };
        panel.Children.Add(_remoteUrlBox);
        panel.Children.Add(Subtle("This repo must be private. ClaudeCounter cannot verify that automatically."));

        _branchBox = new TextBox { Text = string.IsNullOrEmpty(seed.Branch) ? "main" : seed.Branch, Width = 180 };
        panel.Children.Add(FieldRow("Branch", _branchBox, BackupHelpText.Branch));
    }

    private void BuildSyncFolderFields(StackPanel panel, BackupDestination seed, bool isNew)
    {
        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) => await OnBrowseAsync();
        var detect = new Button { Content = "Detect..." };
        detect.Click += async (_, _) => await OnDetectAsync();
        panel.Children.Add(HeaderRow("Sync folder path", BackupHelpText.SyncFolderForThisOs, browse, detect));

        _folderPathBox = new TextBox { Text = seed.FolderPath };
        panel.Children.Add(_folderPathBox);

        // A new destination of a known provider tries to fill the folder in
        // by itself, as on Windows - in the background, see the class remarks.
        if (isNew && string.IsNullOrEmpty(seed.FolderPath))
        {
            Opened += async (_, _) =>
            {
                var candidates = await DetectForProviderAsync();
                if (candidates.Count == 1 && string.IsNullOrWhiteSpace(_folderPathBox.Text))
                    _folderPathBox.Text = candidates[0].Path;
            };
        }
    }

    private void BuildRcloneFields(StackPanel panel, BackupDestination seed)
    {
        _rcloneRemoteBox = new TextBox { Text = seed.RcloneRemote, Width = 240, PlaceholderText = "remote:folder" };
        panel.Children.Add(FieldRow("Rclone remote", _rcloneRemoteBox, BackupHelpText.RcloneRemote));
    }

    private void BuildRetentionSection(StackPanel panel, DestinationKind kind, BackupDestination seed)
    {
        panel.Children.Add(HeaderRow("Retention", BackupHelpText.Retention, bold: true));

        if (kind == DestinationKind.GitHub)
        {
            panel.Children.Add(Subtle("GitHub keeps full history via commits - retention settings do not apply here."));
            return;
        }

        panel.Children.Add(Subtle(
            "When both are ticked, a backup is pruned if EITHER rule would remove it. " +
            "The single most recent backup is never deleted, whatever these settings say."));

        _keepLastEnabled = new CheckBox { Content = "Keep only the most recent", IsChecked = seed.KeepLastCount.HasValue };
        _keepLastCount = Numeric(1, 3650, seed.KeepLastCount ?? 30);
        panel.Children.Add(InlineRow(_keepLastEnabled, _keepLastCount, "backup(s)"));

        _deleteOlderEnabled = new CheckBox { Content = "Delete backups older than", IsChecked = seed.DeleteOlderThanDays.HasValue };
        _deleteOlderDays = Numeric(1, 3650, seed.DeleteOlderThanDays ?? 90);
        panel.Children.Add(InlineRow(_deleteOlderEnabled, _deleteOlderDays, "day(s)"));
    }

    private async Task OnChooseFilesAsync()
    {
        var picker = new BackupPickerDialog(
            DestinationName.Length > 0 ? DestinationName : "this destination", _sourceRoot, Include);
        if (await picker.ShowDialog<bool>(this))
            _includeBox.Text = string.Join(Environment.NewLine, picker.Include);
    }

    private async Task OnBrowseAsync()
    {
        IStorageFolder? start = null;
        var current = _folderPathBox!.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder your sync client (Dropbox, OneDrive, a Google Drive client) or NAS already watches.",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _folderPathBox.Text = path;
    }

    private async Task OnDetectAsync()
    {
        var candidates = await DetectForProviderAsync();
        var dialog = new SyncFolderDetectDialog(candidates);
        if (await dialog.ShowDialog<bool>(this) && dialog.SelectedPath is { } path)
            _folderPathBox!.Text = path;
    }

    private Task<IReadOnlyList<SyncFolderCandidate>> DetectForProviderAsync() =>
        Task.Run(() => SyncFolderScanner.FilterByProvider(SyncFolderScanner.DetectLinux(), _syncProvider));

    private async Task OnOkAsync()
    {
        if (DestinationName.Length == 0)
        {
            await MessageDialog.ShowAsync(this, "Name cannot be empty.", warning: true);
            return;
        }

        if (_kind == DestinationKind.GitHub && BackupDestinationValidation.HasEmbeddedCredential(RemoteUrl))
        {
            await MessageDialog.ShowAsync(this,
                "Remote URL must not embed a credential (e.g. https://user:token@host/...). " +
                "backup.json is never allowed to contain a secret - set up a git credential " +
                "helper (or an SSH key) for this remote instead.", warning: true);
            return;
        }

        if (_kind == DestinationKind.Rclone && BackupDestinationValidation.HasLeadingDash(RcloneRemote))
        {
            await MessageDialog.ShowAsync(this,
                "Rclone remote must not start with '-' - rclone would parse it as an option " +
                "rather than a remote name.", warning: true);
            return;
        }

        if (_kind == DestinationKind.SyncFolder && DestinationEnabled)
        {
            var folderError = SyncFolderPathValidator.Validate(FolderPath, _sourceRoot);
            if (folderError is not null)
            {
                await MessageDialog.ShowAsync(this, folderError, warning: true);
                return;
            }
        }

        Close(true);
    }

    // --- layout helpers ---------------------------------------------------

    private static Control FieldRow(string label, Control input, string help)
    {
        var info = InfoButton.Create(help);
        DockPanel.SetDock(info, Dock.Right);
        var text = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        input.Margin = new Thickness(0, 0, 8, 0);
        return new DockPanel { Children = { info, text, input } };
    }

    private static Control WithInfo(Control control, string help)
    {
        var info = InfoButton.Create(help);
        DockPanel.SetDock(info, Dock.Right);
        return new DockPanel { Children = { info, control } };
    }

    private static Control HeaderRow(string text, string help, params Button[] buttons) =>
        HeaderRow(text, help, bold: false, buttons);

    private static Control HeaderRow(string text, string help, bool bold, params Button[] buttons)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
            right.Children.Add(button);
        right.Children.Add(InfoButton.Create(help));
        DockPanel.SetDock(right, Dock.Right);
        return new DockPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                right,
                new TextBlock
                {
                    Text = text,
                    FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private static Control InlineRow(CheckBox check, NumericUpDown number, string unit)
    {
        void Sync() => number.IsEnabled = check.IsChecked == true;
        check.IsCheckedChanged += (_, _) => Sync();
        Sync();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { check, number, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    private static TextBox MultiLine(IEnumerable<string> lines) => new()
    {
        Text = string.Join(Environment.NewLine, lines),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 76,
    };

    private static TextBlock Subtle(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
    };

    private static NumericUpDown Numeric(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        FormatString = "0",
        Width = 130,
    };

    private static List<string> SplitLines(string? text) =>
        (text ?? "").Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
