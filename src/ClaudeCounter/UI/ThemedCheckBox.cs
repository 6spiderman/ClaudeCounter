using System.Drawing.Drawing2D;

namespace ClaudeCounter.UI;

/// <summary>
/// A checkbox with a fully custom-painted indicator box, themed via Palette.
/// Subclassing CheckBox itself (rather than a plain Control) keeps all of the
/// base class's input handling for free - click-to-toggle, Space-to-toggle,
/// Checked/CheckedChanged and Tab-order participate exactly as on a stock
/// CheckBox; only painting and preferred-size are overridden.
///
/// Extracted out of SettingsForm (where it started as a private nested
/// class) so BackupAdvancedDialog can use the same themed control instead of
/// falling back to a stock CheckBox, whose FlatStyle.Flat rendering is
/// unreadable on the dark palette (a dark box with no visible border and no
/// visible check glyph - ticked and unticked look identical). Both
/// SettingsForm and BackupAdvancedDialog reference this one definition now.
/// </summary>
internal sealed class ThemedCheckBox : CheckBox
{
    private const int BoxSize = 16;
    private const int BoxTextGap = 8;

    private readonly Palette _palette;

    public ThemedCheckBox(Palette palette)
    {
        _palette = palette;
        FlatStyle = FlatStyle.Flat; // GDI+-rendered, not FlatStyle.System - required for OnPaint to be honored
        BackColor = Color.Transparent;
        ForeColor = palette.Fore;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(Text, Font);
        return new Size(
            BoxSize + BoxTextGap + textSize.Width + 2,
            Math.Max(BoxSize, textSize.Height) + 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? _palette.Back);

        var boxRect = new Rectangle(0, (Height - BoxSize) / 2, BoxSize, BoxSize);
        using (var fillBrush = new SolidBrush(Checked ? _palette.Fore : _palette.Back))
            e.Graphics.FillRectangle(fillBrush, boxRect);
        using (var borderPen = new Pen(_palette.Border))
            e.Graphics.DrawRectangle(borderPen, boxRect.X, boxRect.Y, boxRect.Width - 1, boxRect.Height - 1);

        if (Checked)
        {
            // Drawn in the palette's Back color against the Fore-filled
            // box: the same figure/ground pair as the rest of the theme,
            // just inverted, so it reads clearly in both light and dark.
            using var tickPen = new Pen(_palette.Back, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            Point[] tick =
            [
                new Point(boxRect.X + 3, boxRect.Y + 8),
                new Point(boxRect.X + 6, boxRect.Y + 11),
                new Point(boxRect.X + 13, boxRect.Y + 4),
            ];
            e.Graphics.DrawLines(tickPen, tick);
        }

        var textRect = new Rectangle(BoxSize + BoxTextGap, 0, Width - BoxSize - BoxTextGap, Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, _palette.Fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
