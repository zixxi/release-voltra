using System.Runtime.InteropServices;
using SkiaSharp;
using Voltra;

namespace Voltra.Linux;

// Windows uses the same layout with Segoe UI and GDI+. Selawik supplies the
// Segoe-compatible face when rasterizing through Skia on Linux.
internal sealed class OledTextRenderer : IDisposable
{
    private readonly SKBitmap bitmap = new(OledFrame.Width, OledFrame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    private readonly SKTypeface face;
    private readonly byte[] bgra = new byte[OledFrame.Width * OledFrame.Height * 4];
    private readonly SKPaint paint;
    private OledText previous;
    private byte[]? frame;

    internal OledTextRenderer()
    {
        using var font = typeof(OledTextRenderer).Assembly.GetManifestResourceStream("Voltra.SelawikBold.ttf")
            ?? throw new InvalidOperationException("The bundled OLED font is missing.");
        face = SKTypeface.FromStream(font) ?? throw new InvalidOperationException("The bundled OLED font could not be loaded.");
        paint = new SKPaint
        {
            Typeface = face, Color = SKColors.White, IsAntialias = false, SubpixelText = false,
            LcdRenderText = false, HintingLevel = SKPaintHinting.Full,
            TextAlign = SKTextAlign.Center
        };
    }

    internal byte[] Render(OledText text)
    {
        if (frame is not null && previous == text) return frame;
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        DrawLine(canvas, text.Top, OledFrame.SmallSize, OledFrame.TopY);
        DrawLine(canvas, text.Center, text.CenterSize, OledFrame.CenterY);
        DrawLine(canvas, text.Bottom, OledFrame.SmallSize, OledFrame.BottomY);
        canvas.Flush();
        Marshal.Copy(bitmap.GetPixels(), bgra, 0, bgra.Length);
        previous = text;
        return frame = OledFrame.PackBgra(bgra, bitmap.RowBytes, dither: false);
    }

    private void DrawLine(SKCanvas canvas, string text, int size, int y)
    {
        if (text.Length == 0) return;
        paint.TextSize = size;
        // Reserve the same roughly one-sixth-em side padding as DrawString.
        float width = OledFrame.Width - 2 - size / 3f;
        if (paint.MeasureText(text) > width)
        {
            const string ellipsis = "…";
            int length = text.Length;
            while (length > 0 && paint.MeasureText(text[..length] + ellipsis) > width)
                length -= length > 1 && char.IsLowSurrogate(text[length - 1]) ? 2 : 1;
            text = text[..length] + ellipsis;
        }
        // The extra top inset follows the GDI+ baseline measured from the Windows clock preview.
        float baseline = y + MathF.Round(-paint.FontMetrics.Ascent + size * .1f);
        canvas.DrawText(text, OledFrame.Width / 2f, baseline, paint);
    }

    public void Dispose()
    {
        paint.Dispose();
        face.Dispose();
        bitmap.Dispose();
    }
}
