using System.Windows;
using System.Windows.Media;

namespace OhMyPic.Core.Editing;

/// <summary>Capture-mode drawing tools.</summary>
public enum AnnotationKind
{
    Pen,
    Rectangle,
    Arrow,
}

/// <summary>
/// One drawn shape. Points are in image pixel coordinates (continuous: pixel (x, y) covers [x, x+1)×[y, y+1), its centre
/// is (x+0.5, y+0.5)). Pen: one or more points (a single point is a round dot). Rectangle and Arrow: exactly two points
/// (start, end); the arrow head sits at the end point. <see cref="Width"/> is the stroke width in pixels.
/// </summary>
public sealed record Annotation(AnnotationKind Kind, IReadOnlyList<Point> Points, Color Color, double Width);

/// <summary>
/// Paints annotations with a small deterministic rasterizer instead of WPF rendering, so the result does not depend on
/// a UI thread, DPI or the render tier, and tests can compare exact bytes. Every shape is a set of line segments with
/// round caps; coverage of a pixel is <c>clamp(Width/2 + 0.5 − distance, 0, 1)</c> from its centre to the nearest
/// segment, which gives a one-pixel anti-aliased edge and round joins.
/// </summary>
public static class Annotations
{
    /// <summary>Half opening angle between the arrow shaft and each head line.</summary>
    private const double ArrowHeadAngleDegrees = 30;

    /// <summary>Shortest arrow head, so thin arrows stay readable.</summary>
    private const double MinArrowHeadLength = 12;

    /// <summary>Arrow head length per unit of stroke width.</summary>
    private const double ArrowHeadPerWidth = 4;

    /// <summary>
    /// Returns a new image with the annotation painted over (source-over with straight alpha, anti-aliased edges).
    /// The input image is never modified. Painting is clipped to the image, so points outside it are allowed.
    /// </summary>
    /// <exception cref="ArgumentException">Width not finite and &gt; 0, a point not finite, or a wrong point count
    /// (Pen needs at least one, Rectangle and Arrow exactly two).</exception>
    public static RasterImage Apply(RasterImage image, Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotation);
        Validate(annotation);
        byte[] pixels = (byte[])image.Pixels.Clone();
        List<Segment> segments = Segments(annotation);
        if (segments.Count > 0 && annotation.Color.A > 0)
            Paint(pixels, image.Width, image.Height, segments, annotation.Width / 2 + 0.5, annotation.Color);
        return new RasterImage(image.Width, image.Height, pixels);
    }

    private static void Validate(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation.Points, nameof(annotation));
        if (!(annotation.Width > 0) || double.IsInfinity(annotation.Width))
            throw new ArgumentException($"Width must be a finite value greater than 0, got {annotation.Width}.", nameof(annotation));
        int count = annotation.Points.Count;
        switch (annotation.Kind)
        {
            case AnnotationKind.Pen when count < 1:
                throw new ArgumentException("A pen stroke needs at least one point.", nameof(annotation));
            case AnnotationKind.Rectangle or AnnotationKind.Arrow when count != 2:
                throw new ArgumentException($"{annotation.Kind} needs exactly two points, got {count}.", nameof(annotation));
            case AnnotationKind.Pen or AnnotationKind.Rectangle or AnnotationKind.Arrow:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(annotation), annotation.Kind, "Unknown annotation kind.");
        }
        foreach (Point p in annotation.Points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                throw new ArgumentException($"Point ({p.X}, {p.Y}) is not finite.", nameof(annotation));
        }
    }

    /// <summary>The shape as line segments. A zero-length arrow has none (no direction to point in).</summary>
    private static List<Segment> Segments(Annotation annotation)
    {
        IReadOnlyList<Point> p = annotation.Points;
        var segments = new List<Segment>();
        switch (annotation.Kind)
        {
            case AnnotationKind.Pen:
                if (p.Count == 1)
                    segments.Add(new Segment(p[0], p[0]));
                for (int i = 1; i < p.Count; i++)
                    segments.Add(new Segment(p[i - 1], p[i]));
                break;
            case AnnotationKind.Rectangle:
            {
                var a = new Point(p[0].X, p[0].Y);
                var b = new Point(p[1].X, p[0].Y);
                var c = new Point(p[1].X, p[1].Y);
                var d = new Point(p[0].X, p[1].Y);
                segments.Add(new Segment(a, b));
                segments.Add(new Segment(b, c));
                segments.Add(new Segment(c, d));
                segments.Add(new Segment(d, a));
                break;
            }
            case AnnotationKind.Arrow:
            {
                Point start = p[0], end = p[1];
                double dx = start.X - end.X, dy = start.Y - end.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length == 0)
                    break;
                segments.Add(new Segment(start, end));
                double head = Math.Min(Math.Max(annotation.Width * ArrowHeadPerWidth, MinArrowHeadLength), length);
                // Unit vector from the tip back along the shaft, rotated by ± the head angle.
                double ux = dx / length, uy = dy / length;
                double angle = ArrowHeadAngleDegrees * Math.PI / 180;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                segments.Add(new Segment(end, new Point(end.X + head * (ux * cos - uy * sin), end.Y + head * (ux * sin + uy * cos))));
                segments.Add(new Segment(end, new Point(end.X + head * (ux * cos + uy * sin), end.Y + head * (-ux * sin + uy * cos))));
                break;
            }
        }
        return segments;
    }

    /// <summary>
    /// Builds a coverage mask over the shape's bounding box (clipped to the image), taking the maximum over segments so
    /// joints and crossings are not blended twice, then blends the colour once per pixel. Each segment only visits the
    /// pixels of its own expanded bounding box, so a long pen stroke costs about its painted area, not area × points.
    /// </summary>
    /// <param name="reach">Distance from a segment at which coverage drops to 0 (half the width plus half a pixel).</param>
    private static void Paint(byte[] pixels, int width, int height, List<Segment> segments, double reach, Color color)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (Segment s in segments)
        {
            minX = Math.Min(minX, Math.Min(s.A.X, s.B.X));
            maxX = Math.Max(maxX, Math.Max(s.A.X, s.B.X));
            minY = Math.Min(minY, Math.Min(s.A.Y, s.B.Y));
            maxY = Math.Max(maxY, Math.Max(s.A.Y, s.B.Y));
        }
        var (left, right) = PixelSpan(minX - reach, maxX + reach, width);
        var (top, bottom) = PixelSpan(minY - reach, maxY + reach, height);
        if (left > right || top > bottom)
            return;
        int maskWidth = right - left + 1;
        float[] mask = new float[maskWidth * (bottom - top + 1)];
        double reachSquared = reach * reach;

        foreach (Segment s in segments)
        {
            var (x0, x1) = PixelSpan(Math.Min(s.A.X, s.B.X) - reach, Math.Max(s.A.X, s.B.X) + reach, width);
            var (y0, y1) = PixelSpan(Math.Min(s.A.Y, s.B.Y) - reach, Math.Max(s.A.Y, s.B.Y) + reach, height);
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
            double lengthSquared = dx * dx + dy * dy;
            for (int y = y0; y <= y1; y++)
            {
                double py = y + 0.5 - s.A.Y;
                int row = (y - top) * maskWidth - left;
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5 - s.A.X;
                    double t = lengthSquared > 0 ? Math.Clamp((px * dx + py * dy) / lengthSquared, 0, 1) : 0;
                    double ex = px - t * dx, ey = py - t * dy;
                    double distanceSquared = ex * ex + ey * ey;
                    if (distanceSquared >= reachSquared)
                        continue;
                    float coverage = (float)Math.Min(reach - Math.Sqrt(distanceSquared), 1);
                    if (coverage > mask[row + x])
                        mask[row + x] = coverage;
                }
            }
        }

        double colorAlpha = color.A / 255.0;
        for (int y = top; y <= bottom; y++)
        {
            int row = (y - top) * maskWidth - left;
            for (int x = left; x <= right; x++)
            {
                float coverage = mask[row + x];
                if (coverage <= 0)
                    continue;
                double a = colorAlpha * coverage;
                int i = (y * width + x) * 4;
                double baseAlpha = pixels[i + 3] / 255.0;
                double keep = baseAlpha * (1 - a);
                double outAlpha = a + keep;
                pixels[i] = ToByte((color.B * a + pixels[i] * keep) / outAlpha);
                pixels[i + 1] = ToByte((color.G * a + pixels[i + 1] * keep) / outAlpha);
                pixels[i + 2] = ToByte((color.R * a + pixels[i + 2] * keep) / outAlpha);
                pixels[i + 3] = ToByte(outAlpha * 255);
            }
        }
    }

    /// <summary>
    /// First and last pixel index whose centre lies within [<paramref name="low"/>, <paramref name="high"/>], clipped to
    /// [0, <paramref name="size"/> − 1]. Empty (first &gt; last) when the span misses the image. Clamping happens in
    /// double so far-away points never overflow the int conversion.
    /// </summary>
    private static (int First, int Last) PixelSpan(double low, double high, int size)
    {
        double first = Math.Clamp(Math.Ceiling(low - 0.5), 0, size);
        double last = Math.Clamp(Math.Floor(high - 0.5), -1, size - 1);
        return ((int)first, (int)last);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private readonly record struct Segment(Point A, Point B);
}
