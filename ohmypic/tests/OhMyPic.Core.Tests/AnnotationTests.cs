using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using OhMyPic.Core.Editing;

namespace OhMyPic.Core.Tests;

/// <summary>Capture annotation rasterizer and its undo step (OP-E52).</summary>
public sealed class AnnotationTests : TestBase
{
    private static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
    private static readonly (byte B, byte G, byte R, byte A) Gray = (128, 128, 128, 255);

    private static RasterImage Solid(int width, int height, byte value) =>
        RasterImage.FromBitmap(Img.Create(width, height, (_, _) => (value, value, value, 255)));

    /// <summary>Colourful image whose alpha cycles through many values.</summary>
    private static RasterImage Translucent(int width, int height) => RasterImage.FromBitmap(Img.Create(width, height, (x, y) =>
        ((byte)(x * 5 % 256), (byte)(y * 7 % 256), (byte)((x + y) * 3 % 256), (byte)((x * 11 + y * 3) % 256))));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static (byte B, byte G, byte R, byte A) At(RasterImage image, int x, int y) => Img.At(image.Pixels, image.Width, x, y);

    private static Annotation Stroke(double width, params Point[] points) => new(AnnotationKind.Pen, points, Colors.Red, width);

    private static double Distance(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
        double ex = p.X - a.X - t * dx, ey = p.Y - a.Y - t * dy;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>
    /// Every pixel whose centre is farther than <paramref name="limit"/> from all segments must be unchanged.
    /// Returns the number of changed pixels.
    /// </summary>
    private static int AssertUnchangedBeyond(RasterImage original, RasterImage result, IReadOnlyList<(Point A, Point B)> segments, double limit)
    {
        Assert.Equal((original.Width, original.Height), (result.Width, result.Height));
        int changed = 0;
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                int i = (y * original.Width + x) * 4;
                if (original.Pixels.AsSpan(i, 4).SequenceEqual(result.Pixels.AsSpan(i, 4)))
                    continue;
                changed++;
                var centre = new Point(x + 0.5, y + 0.5);
                double distance = segments.Min(s => Distance(centre, s.A, s.B));
                Assert.True(distance <= limit, $"pixel {x},{y} changed at distance {distance:0.###} (limit {limit})");
            }
        }
        return changed;
    }

    private static (Point A, Point B)[] RectangleEdges(Point p, Point q) =>
    [
        (new Point(p.X, p.Y), new Point(q.X, p.Y)),
        (new Point(q.X, p.Y), new Point(q.X, q.Y)),
        (new Point(q.X, q.Y), new Point(p.X, q.Y)),
        (new Point(p.X, q.Y), new Point(p.X, p.Y)),
    ];

    /// <summary>Shaft plus the two head lines (±30° from the shaft, the given head length) of a right-pointing arrow.</summary>
    private static (Point A, Point B)[] ArrowSegments(Point start, Point end, double head)
    {
        double angle = Math.PI / 6;
        return
        [
            (start, end),
            (end, new Point(end.X - head * Math.Cos(angle), end.Y - head * Math.Sin(angle))),
            (end, new Point(end.X - head * Math.Cos(angle), end.Y + head * Math.Sin(angle))),
        ];
    }

    [Fact]
    public void OP_E52_Pen_Paints_Line_Centre_And_Leaves_Pixels_Beyond_The_Width()
    {
        RasterImage gray = Solid(64, 64, 128);
        var a = new Point(8, 32.5);
        var b = new Point(56, 32.5);
        RasterImage line = Annotations.Apply(gray, Stroke(5, a, b));
        for (int x = 8; x < 56; x++)
        {
            for (int y = 30; y <= 34; y++)
                Assert.Equal(Red, At(line, x, y));
        }
        // Round cap: half a width past the end point.
        Assert.Equal(Red, At(line, 57, 32));
        Assert.Equal(Gray, At(line, 32, 29));
        Assert.Equal(Gray, At(line, 32, 35));
        Assert.True(AssertUnchangedBeyond(gray, line, [(a, b)], 2.5 + 1) > 0);

        // Polyline with round joins: every vertex and segment midpoint is painted.
        Point[] zigzag = [new(10.5, 10.5), new(30.5, 50.5), new(50.5, 10.5)];
        RasterImage poly = Annotations.Apply(gray, Stroke(4, zigzag));
        foreach (var (x, y) in new[] { (10, 10), (30, 50), (50, 10), (20, 30), (40, 30) })
            Assert.Equal(Red, At(poly, x, y));
        Assert.Equal(Gray, At(poly, 30, 30));
        AssertUnchangedBeyond(gray, poly, [(zigzag[0], zigzag[1]), (zigzag[1], zigzag[2])], 2 + 1);

        // A single point is a round dot of diameter Width.
        var dot = new Point(20.5, 20.5);
        RasterImage dotted = Annotations.Apply(gray, Stroke(6, dot));
        Assert.Equal(Red, At(dotted, 20, 20));
        Assert.Equal(Red, At(dotted, 22, 20));
        Assert.Equal(Gray, At(dotted, 23, 23));
        Assert.True(AssertUnchangedBeyond(gray, dotted, [(dot, dot)], 3 + 1) > 0);

        // The edge is anti-aliased: a pixel centre 2.25 from a width-4 line gets a quarter of the colour.
        RasterImage soft = Annotations.Apply(gray, Stroke(4, new Point(8, 40.25), new Point(56, 40.25)));
        var edge = At(soft, 30, 42);
        Assert.True(edge.R > 128 && edge.R < 255 && edge.G < 128 && edge.G > 0, $"edge pixel {edge}");
        Assert.Equal(Gray, At(soft, 30, 43));
    }

    [Fact]
    public void OP_E52_Rectangle_Is_An_Outline()
    {
        RasterImage gray = Solid(64, 64, 128);
        var p = new Point(10.5, 10.5);
        var q = new Point(50.5, 40.5);
        RasterImage rect = Annotations.Apply(gray, new Annotation(AnnotationKind.Rectangle, [p, q], Colors.Red, 3));
        foreach (var (x, y) in new[] { (30, 10), (10, 25), (50, 25), (30, 40), (10, 10), (50, 10), (50, 40), (10, 40) })
            Assert.Equal(Red, At(rect, x, y));
        Assert.Equal(Gray, At(rect, 30, 25));
        Assert.Equal(Gray, At(rect, 13, 25));
        Assert.True(AssertUnchangedBeyond(gray, rect, RectangleEdges(p, q), 1.5 + 1) > 0);

        // Corner order does not matter.
        RasterImage swapped = Annotations.Apply(gray, new Annotation(AnnotationKind.Rectangle, [q, p], Colors.Red, 3));
        Assert.Equal(Hash(rect.Pixels), Hash(swapped.Pixels));
    }

    [Fact]
    public void OP_E52_Arrow_Paints_Shaft_And_Both_Head_Lines()
    {
        RasterImage gray = Solid(64, 64, 128);

        // Width 3: head length is the minimum 12.
        var start = new Point(10.5, 32.5);
        var end = new Point(50.5, 32.5);
        RasterImage arrow = Annotations.Apply(gray, new Annotation(AnnotationKind.Arrow, [start, end], Colors.Red, 3));
        Assert.Equal(Red, At(arrow, 50, 32));
        Assert.Equal(Red, At(arrow, 30, 32));
        Assert.Equal(Red, At(arrow, 45, 29));
        Assert.Equal(Red, At(arrow, 45, 35));
        Assert.Equal(Gray, At(arrow, 30, 29));
        Assert.Equal(Gray, At(arrow, 45, 40));
        Assert.True(AssertUnchangedBeyond(gray, arrow, ArrowSegments(start, end, 12), 1.5 + 1) > 0);

        // Width 5: head length is Width * 4 = 20, so the head reaches a pixel a 12-pixel head would miss.
        var wideEnd = new Point(60.5, 32.5);
        RasterImage wide = Annotations.Apply(gray, new Annotation(AnnotationKind.Arrow, [start, wideEnd], Colors.Red, 5));
        Assert.Equal(Red, At(wide, 44, 23));
        Assert.Equal(Red, At(wide, 44, 41));
        AssertUnchangedBeyond(gray, wide, ArrowSegments(start, wideEnd, 20), 2.5 + 1);

        // Short shaft: the head is clamped to the shaft length (5), not 12.
        var shortEnd = new Point(15.5, 32.5);
        RasterImage stub = Annotations.Apply(gray, new Annotation(AnnotationKind.Arrow, [start, shortEnd], Colors.Red, 3));
        Assert.Equal(Red, At(stub, 15, 32));
        Assert.Equal(Gray, At(stub, 5, 26));
        Assert.Equal(Gray, At(stub, 5, 38));
        AssertUnchangedBeyond(gray, stub, ArrowSegments(start, shortEnd, 5), 1.5 + 1);

        // A zero-length arrow has no direction and paints nothing (still a new image).
        RasterImage none = Annotations.Apply(gray, new Annotation(AnnotationKind.Arrow, [end, end], Colors.Red, 3));
        Assert.NotSame(gray, none);
        Assert.NotSame(gray.Pixels, none.Pixels);
        Assert.Equal(Hash(gray.Pixels), Hash(none.Pixels));
    }

    [Fact]
    public void OP_E52_Input_Is_Unchanged_And_Output_Is_Deterministic()
    {
        RasterImage source = Translucent(48, 40);
        string before = Hash(source.Pixels);
        Annotation[] annotations =
        [
            new(AnnotationKind.Pen, [new Point(3.2, 4.7), new Point(20.9, 30.1), new Point(44.4, 8.8)], Color.FromArgb(200, 10, 220, 30), 3.5),
            new(AnnotationKind.Rectangle, [new Point(5.3, 6.1), new Point(40.7, 33.9)], Colors.Blue, 2.25),
            new(AnnotationKind.Arrow, [new Point(40, 35), new Point(8.6, 12.4)], Color.FromArgb(128, 255, 0, 0), 4),
        ];
        foreach (Annotation annotation in annotations)
        {
            RasterImage first = Annotations.Apply(source, annotation);
            RasterImage second = Annotations.Apply(source, annotation);
            Assert.NotSame(source.Pixels, first.Pixels);
            Assert.NotEqual(before, Hash(first.Pixels));
            Assert.Equal(Hash(first.Pixels), Hash(second.Pixels));
            Assert.Equal(before, Hash(source.Pixels));
        }
    }

    [Fact]
    public void OP_E52_Points_Outside_The_Image_Are_Clipped()
    {
        RasterImage gray = Solid(64, 64, 128);
        Point[] stroke = [new(-20, 10.5), new(30.5, 10.5), new(30.5, 1000), new(1e9, -1e9)];
        RasterImage clipped = Annotations.Apply(gray, Stroke(4, stroke));
        Assert.Equal(Red, At(clipped, 0, 10));
        Assert.Equal(Red, At(clipped, 30, 63));
        Assert.Equal(Gray, At(clipped, 0, 63));
        var segments = new List<(Point A, Point B)>();
        for (int i = 1; i < stroke.Length; i++)
            segments.Add((stroke[i - 1], stroke[i]));
        Assert.True(AssertUnchangedBeyond(gray, clipped, segments, 2 + 1) > 0);

        foreach (Annotation outside in new[]
                 {
                     new Annotation(AnnotationKind.Rectangle, [new Point(-100, -100), new Point(-50, -50)], Colors.Red, 3),
                     new Annotation(AnnotationKind.Arrow, [new Point(200, 10), new Point(100, 10)], Colors.Red, 3),
                     Stroke(10, new Point(-1e12, 1e12)),
                 })
            Assert.Equal(Hash(gray.Pixels), Hash(Annotations.Apply(gray, outside).Pixels));
    }

    [Fact]
    public void OP_E52_Invalid_Arguments_Throw()
    {
        RasterImage gray = Solid(16, 16, 128);
        var p = new Point(4, 4);
        var q = new Point(10, 10);
        Annotation[] invalid =
        [
            Stroke(0, p, q),
            Stroke(-1, p, q),
            Stroke(double.NaN, p, q),
            Stroke(double.PositiveInfinity, p, q),
            Stroke(3),
            Stroke(3, p, new Point(double.NaN, 1)),
            Stroke(3, p, new Point(1, double.PositiveInfinity)),
            new(AnnotationKind.Rectangle, [p], Colors.Red, 3),
            new(AnnotationKind.Rectangle, [p, q, p], Colors.Red, 3),
            new(AnnotationKind.Arrow, [p], Colors.Red, 3),
            new(AnnotationKind.Arrow, [p, q, p], Colors.Red, 3),
            new(AnnotationKind.Arrow, [p, new Point(double.NegativeInfinity, 2)], Colors.Red, 3),
            new((AnnotationKind)99, [p, q], Colors.Red, 3),
        ];
        foreach (Annotation annotation in invalid)
            Assert.ThrowsAny<ArgumentException>(() => Annotations.Apply(gray, annotation));
        Assert.Throws<ArgumentNullException>(() => Annotations.Apply(gray, null!));
        Assert.Throws<ArgumentNullException>(() => Annotations.Apply(null!, Stroke(3, p)));
    }

    [Fact]
    public void OP_E52_Translucent_Colour_Blends_With_Straight_Alpha()
    {
        Color halfRed = Color.FromArgb(128, 255, 0, 0);
        double a = 128 / 255.0;
        Annotation line = new(AnnotationKind.Pen, [new Point(4, 8.5), new Point(28, 8.5)], halfRed, 3);

        RasterImage gray = Solid(32, 16, 128);
        var blended = At(Annotations.Apply(gray, line), 16, 8);
        byte red = (byte)Math.Round(255 * a + 128 * (1 - a), MidpointRounding.AwayFromZero);
        byte rest = (byte)Math.Round(128 * (1 - a), MidpointRounding.AwayFromZero);
        Assert.Equal((rest, rest, red, (byte)255), blended);
        Assert.True(blended.R > 128 && blended.R < 255 && blended.G > 0 && blended.G < 128, $"blended {blended}");

        // On a transparent pixel the colour keeps its own value; only alpha reflects the transparency.
        RasterImage clear = RasterImage.FromBitmap(Img.Create(32, 16, (_, _) => (0, 0, 0, 0)));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)128), At(Annotations.Apply(clear, line), 16, 8));
        Assert.Equal(Red, At(Annotations.Apply(clear, line with { Color = Colors.Red }), 16, 8));
    }

    [Fact]
    public void OP_E52_EditSession_Draw_Is_One_Undo_Step()
    {
        RasterImage original = RasterImage.FromBitmap(Img.Palette(48, 32));
        string originalHash = Hash(original.Pixels);
        var session = new EditSession(original);
        Annotation[] annotations =
        [
            Stroke(4, new Point(2, 2), new Point(40, 20), new Point(10, 30)),
            new(AnnotationKind.Rectangle, [new Point(5.5, 5.5), new Point(30.5, 25.5)], Colors.Lime, 2),
            new(AnnotationKind.Arrow, [new Point(44, 4), new Point(12, 24)], Colors.Blue, 3),
        ];
        var drawn = new List<string>();
        for (int i = 0; i < annotations.Length; i++)
        {
            session.Draw(annotations[i]);
            Assert.Equal(i + 1, session.UndoCount);
            Assert.False(session.CanRedo);
            drawn.Add(Hash(session.Base.Pixels));
        }
        Assert.Equal(Hash(Annotations.Apply(original, annotations[0]).Pixels), drawn[0]);
        Assert.Equal(3, drawn.Distinct().Count());

        for (int i = annotations.Length - 1; i >= 0; i--)
        {
            session.Undo();
            Assert.Equal(i == 0 ? originalHash : drawn[i - 1], Hash(session.Base.Pixels));
        }
        Assert.False(session.CanUndo);
        Assert.Equal(originalHash, Hash(session.Render().Pixels));

        for (int i = 0; i < annotations.Length; i++)
        {
            session.Redo();
            Assert.Equal(drawn[i], Hash(session.Base.Pixels));
        }
        Assert.Equal(originalHash, Hash(original.Pixels));
        Assert.Throws<ArgumentNullException>(() => session.Draw(null!));
        Assert.Equal(annotations.Length, session.UndoCount);
    }
}
