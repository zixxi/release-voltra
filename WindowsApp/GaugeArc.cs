using System.Drawing;

namespace Voltra;

internal static class GaugeArc
{
    internal const int Segments = 90;
    internal const float Start = 135, Sweep = 270;

    internal static float Degrees(float percent) =>
        float.IsFinite(percent) ? Math.Clamp(percent, 0, 100) * (Sweep / 100) : 0;

    internal static PointF Point(PointF center, float angle, float radius)
    {
        double radians = angle * Math.PI / 180;
        return new(center.X + (float)Math.Cos(radians) * radius, center.Y + (float)Math.Sin(radians) * radius);
    }

    // A subpixel sweep can collapse to coincident endpoints inside native GDI+.
    // Build a bounded polyline instead. Three-degree chords differ from the arc
    // by less than a tenth of a pixel even at a 250px radius.
    internal static int Fill(Span<PointF> points, PointF center, float radius, float degrees)
    {
        if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(degrees) || degrees <= 0 ||
            radius * degrees * (MathF.PI / 180) < 0.5f) return 0;
        degrees = Math.Min(degrees, Sweep);
        int count = (int)Math.Ceiling(degrees / (Sweep / Segments));
        for (int i = 0; i <= count; i++)
            points[i] = Point(center, Start + degrees * i / count, radius);
        return count + 1;
    }
}
