using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// "Detect..." for a sync-folder destination: the candidates
/// SyncFolderScanner found for this provider, to pick one. Linux port of the
/// Windows dialog, same wording.
/// </summary>
public sealed class SyncFolderDetectDialog : Window
{
    private readonly IReadOnlyList<SyncFolderCandidate> _candidates;
    private readonly ListBox _list;

    /// <summary>The chosen folder once the dialog closes with true.</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>Show with <c>await dialog.ShowDialog&lt;bool&gt;(owner)</c>.</summary>
    public SyncFolderDetectDialog(IReadOnlyList<SyncFolderCandidate> candidates)
    {
        _candidates = candidates;

        Title = "Detected Sync Folders";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox
        {
            ItemsSource = candidates.Select(c => $"{c.DisplayName}: {c.Path}").ToArray(),
            SelectedIndex = candidates.Count > 0 ? 0 : -1,
            MaxHeight = 220,
            IsVisible = candidates.Count > 0,
        };

        var use = new Button { Content = "Use this folder", IsDefault = true, IsEnabled = candidates.Count > 0 };
        use.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        _list.DoubleTapped += (_, _) => Accept();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = candidates.Count > 0
                        ? "Pick a folder your sync client or NAS already watches:"
                        : "No sync folder was found automatically on this machine. " +
                          "Use Browse... instead, or type the path directly.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, use },
                },
            },
        };
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _candidates.Count)
            return;
        SelectedPath = _candidates[_list.SelectedIndex].Path;
        Close(true);
    }
}
