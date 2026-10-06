using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClaudeCounter.UI;

/// <summary>
/// The full backup guide (Settings -> Backup -> Help), Linux wording - see
/// <see cref="BackupHelpText.FullGuideForThisOs"/>.
/// </summary>
public sealed class BackupHelpDialog : Window
{
    public BackupHelpDialog()
    {
        Title = "Backup Help";
        Width = 520;
        Height = 560;
        MinHeight = 320;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var bottom = new Border { Padding = new Thickness(16, 10), Child = close };
        DockPanel.SetDock(bottom, Dock.Bottom);

        Content = new DockPanel
        {
            Children =
            {
                bottom,
                new ScrollViewer
                {
                    Content = new SelectableTextBlock
                    {
                        // The guide's line breaks are \r\n; Avalonia treats \r as one too.
                        Text = BackupHelpText.FullGuideForThisOs.Replace("\r\n", "\n", StringComparison.Ordinal),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(16),
                    },
                },
            },
        };
    }
}
