using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ClaudeCounter.UI;

/// <summary>
/// "Add..." in Manage destinations: pick what kind of destination to add.
/// Linux port of the Windows dialog; the options come from the shared
/// <see cref="BackupDestinationNaming.KindOptions"/>.
/// </summary>
public sealed class BackupDestinationKindDialog : Window
{
    private readonly ListBox _list;

    /// <summary>The chosen option once the dialog closes with true.</summary>
    public BackupDestinationNaming.KindOption? Selected { get; private set; }

    /// <summary>Show with <c>await dialog.ShowDialog&lt;bool&gt;(owner)</c>.</summary>
    public BackupDestinationKindDialog()
    {
        Title = "Add a Backup Destination";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox
        {
            ItemsSource = BackupDestinationNaming.KindOptions.Select(o => o.Label).ToArray(),
            SelectedIndex = 0,
            MaxHeight = 260,
        };

        var add = new Button { Content = "Add...", IsDefault = true };
        add.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        _list.DoubleTapped += (_, _) => Accept();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "What kind of destination do you want to add?" },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, add },
                },
            },
        };
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0)
            return;
        Selected = BackupDestinationNaming.KindOptions[_list.SelectedIndex];
        Close(true);
    }
}
