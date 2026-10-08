using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using VarlatchKit;

namespace Varlatch;

/// <summary>
/// The Varlatch mark for the notification area: in the taskbar's own
/// monochrome (white on a dark taskbar, near-black on a light one), with a
/// small amber badge while a credential is expiring and a red one when
/// signed out, expired, or the CLI is unusable (the mark is dimmed then).
/// Color stays in the badge, as on macOS.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Lazy<Bitmap> Mark = new(() =>
    {
        using var stream = typeof(TrayIconRenderer).Assembly.GetManifestResourceStream("TrayMark.png")
            ?? throw new InvalidOperationException("TrayMark.png is not embedded");
        return new Bitmap(stream);
    });

    private static readonly Color Amber = Color.FromArgb(255, 0xFF, 0x9F, 0x0A);
    private static readonly Color RedOnDark = Color.FromArgb(255, 0xFF, 0x45, 0x3A);
    private static readonly Color RedOnLight = Color.FromArgb(255, 0xE5, 0x2B, 0x22);

    /// <summary>An icon handle the caller destroys.</summary>
    public static IntPtr Render(OverallState state, int size, bool lightTaskbar)
    {
        using var bitmap = Draw(state, size, lightTaskbar);
        return bitmap.GetHicon();
    }

    public static Bitmap Draw(OverallState state, int size, bool lightTaskbar)
    {
        var badge = state.Badge();
        var dimmed = state.CliUnusable();
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        // The mark keeps its aspect ratio, as wide as the icon, centered.
        var mark = Mark.Value;
        var width = (float)size;
        var height = width * mark.Height / mark.Width;
        var rect = new RectangleF(0, (size - height) / 2, width, height);
        var ink = lightTaskbar ? Color.FromArgb(255, 0x1B, 0x1B, 0x1B) : Color.White;
        var alpha = dimmed ? 0.55f : 1f;
        using (var attributes = new ImageAttributes())
        {
            // The symbolic mark is white: scale it to the ink color.
            attributes.SetColorMatrix(new ColorMatrix(
            [
                [ink.R / 255f, 0, 0, 0, 0],
                [0, ink.G / 255f, 0, 0, 0],
                [0, 0, ink.B / 255f, 0, 0],
                [0, 0, 0, alpha, 0],
                [0, 0, 0, 0, 1],
            ]));
            g.DrawImage(mark, Rectangle.Round(rect), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
        }

        if (badge != Badge.None)
        {
            var color = badge == Badge.Warning ? Amber : lightTaskbar ? RedOnLight : RedOnDark;
            var d = MathF.Max(5f, MathF.Round(size * 0.44f));
            var dot = new RectangleF(size - d, 0, d, d);
            // A clear ring around the badge, so it reads against the mark.
            var ring = MathF.Max(1f, size / 16f * 1.5f);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
                g.FillEllipse(clear, RectangleF.Inflate(dot, ring, ring));
            g.CompositingMode = CompositingMode.SourceOver;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, dot);
        }
        return bitmap;
    }
}
