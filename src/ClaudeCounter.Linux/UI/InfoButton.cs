using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ClaudeCounter.UI;

/// <summary>
/// The small "?" next to a backup field, like the Windows dialogs' info
/// buttons: hover for the field's help as a tooltip, click to read it in a
/// dialog (tooltips vanish too quickly for the longer topics).
/// </summary>
public static class InfoButton
{
    public static Button Create(string helpText)
    {
        var button = new Button
        {
            Content = "?",
            Padding = new Thickness(7, 0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = helpText,
        };
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is Window owner)
                await MessageDialog.ShowAsync(owner, helpText);
        };
        return button;
    }
}
