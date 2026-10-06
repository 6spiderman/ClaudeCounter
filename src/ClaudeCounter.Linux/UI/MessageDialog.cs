using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClaudeCounter.UI;

/// <summary>
/// The Linux build's stand-in for WinForms' MessageBox: Avalonia has none.
/// Modal over its owner, sized to its text, Enter/Esc to answer.
/// </summary>
public sealed class MessageDialog : Window
{
    private bool _result;

    private MessageDialog(string message, bool warning, string okText, string? cancelText)
    {
        Title = "ClaudeCounter";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { _result = true; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }
        else
        {
            ok.IsCancel = true;
        }
        buttons.Children.Add(ok);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        // Only set for a warning: assigning null would not fall back to the
        // theme's text colour, it would draw the message with no brush at all.
        if (warning)
            text.Foreground = new SolidColorBrush(BandPalette.BandColor(Band.Amber));

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { text, buttons },
        };
    }

    /// <summary>An information or warning message with a single OK.</summary>
    public static Task ShowAsync(Window owner, string message, bool warning = false) =>
        new MessageDialog(message, warning, "OK", null).ShowDialog(owner);

    /// <summary>
    /// A question with no owner window - for the tray menu, which has none to
    /// be modal over. Centred on screen and kept on top; true when the user
    /// chose <paramref name="yesText"/>.
    /// </summary>
    public static Task<bool> ConfirmStandaloneAsync(string message, string yesText = "Yes", string noText = "No")
    {
        var dialog = new MessageDialog(message, warning: false, yesText, noText)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };
        var answered = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => answered.TrySetResult(dialog._result);
        dialog.Show();
        dialog.Activate();
        return answered.Task;
    }

    /// <summary>A question; true when the user chose <paramref name="yesText"/>.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string yesText = "Yes", string noText = "No", bool warning = false)
    {
        var dialog = new MessageDialog(message, warning, yesText, noText);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
