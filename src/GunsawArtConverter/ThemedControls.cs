using System.Drawing;
using System.Windows.Forms;

namespace GunsawArtConverter;

internal static class ThemeColors
{
    public static readonly Color ControlBack = Color.FromArgb(48, 53, 61);
    public static readonly Color ControlBorder = Color.FromArgb(100, 107, 118);
    public static readonly Color ButtonBack = Color.FromArgb(76, 82, 91);
    public static readonly Color ButtonHover = Color.FromArgb(92, 99, 109);
    public static readonly Color ListSelection = Color.FromArgb(89, 96, 106);
}

internal sealed class ThemedNumericUpDown : NumericUpDown
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x000F || message.Msg == 0x0085)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            ControlPaint.DrawBorder(graphics, ClientRectangle, ThemeColors.ControlBorder, ButtonBorderStyle.Solid);
        }
    }
}

internal sealed class ThemedComboBox : ComboBox
{
    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DrawItem += DrawListItem;
    }

    private void DrawListItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0)
            return;
        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? ThemeColors.ListSelection : ThemeColors.ControlBack);
        using var foreground = new SolidBrush(ForeColor);
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        eventArgs.Graphics.DrawString(GetItemText(Items[eventArgs.Index]), Font, foreground, eventArgs.Bounds);
        eventArgs.DrawFocusRectangle();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x000F || message.Msg == 0x0085)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            ControlPaint.DrawBorder(graphics, ClientRectangle, ThemeColors.ControlBorder, ButtonBorderStyle.Solid);
        }
    }
}

internal sealed class ThemedTextBox : TextBox
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x000F || message.Msg == 0x0085)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            ControlPaint.DrawBorder(graphics, ClientRectangle, ThemeColors.ControlBorder, ButtonBorderStyle.Solid);
        }
    }
}