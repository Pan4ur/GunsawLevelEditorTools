using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GunsawArtConverter;

internal sealed class PreviewBox : Control
{
    public Bitmap? Image { get; set; }

    public PreviewBox()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 23, 28);
        Dock = DockStyle.Fill;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Image == null)
        {
            using var brush = new SolidBrush(Color.FromArgb(150, 160, 175));
            using var font = new Font(Font.FontFamily, 14f);
            var text = "Drop an image here";
            var size = e.Graphics.MeasureString(text, font);
            e.Graphics.DrawString(text, font, brush, (Width - size.Width) / 2, (Height - size.Height) / 2);
            return;
        }

        var scale = System.Math.Min((Width - 24f) / Image.Width, (Height - 24f) / Image.Height);
        var w = Image.Width * scale;
        var h = Image.Height * scale;
        var rect = new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
        e.Graphics.InterpolationMode =
            scale >= 4 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(Image, rect);
    }
}