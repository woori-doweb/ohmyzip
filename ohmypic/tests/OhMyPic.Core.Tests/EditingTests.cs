using System.Windows;
using System.Windows.Media.Imaging;
using OhMyPic.Core.Editing;

namespace OhMyPic.Core.Tests;

public sealed class EditingTests : TestBase
{
    private static RasterImage Raster(BitmapSource bitmap) => RasterImage.FromBitmap(bitmap);

    /// <summary>Colourful image whose alpha cycles through every value 0..255.</summary>
    private static RasterImage Translucent(int width, int height) => Raster(Img.Create(width, height, (x, y) =>
        ((byte)(x * 5 % 256), (byte)(y * 7 % 256), (byte)((x + y) * 3 % 256), (byte)((x * 11 + y * 3) % 256))));

    private static RasterImage Solid(int width, int height, byte value) =>
        Raster(Img.Create(width, height, (_, _) => (value, value, value, 255)));

    private static double MeanLuma(RasterImage image)
    {
        double sum = 0;
        byte[] p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
            sum += 0.114 * p[i] + 0.587 * p[i + 1] + 0.299 * p[i + 2];
        return sum / (p.Length / 4);
    }

    private static BitmapSource DecodeBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static AdjustmentSettings Setting(string name, int value) => name switch
    {
        nameof(AdjustmentSettings.Brightness) => new AdjustmentSettings { Brightness = value },
        nameof(AdjustmentSettings.Contrast) => new AdjustmentSettings { Contrast = value },
        nameof(AdjustmentSettings.Saturation) => new AdjustmentSettings { Saturation = value },
        nameof(AdjustmentSettings.Warmth) => new AdjustmentSettings { Warmth = value },
        nameof(AdjustmentSettings.Hue) => new AdjustmentSettings { Hue = value },
        nameof(AdjustmentSettings.Sharpen) => new AdjustmentSettings { Sharpen = value },
        nameof(AdjustmentSettings.Vignette) => new AdjustmentSettings { Vignette = value },
        nameof(AdjustmentSettings.Grayscale) => new AdjustmentSettings { Grayscale = value },
        nameof(AdjustmentSettings.Sepia) => new AdjustmentSettings { Sepia = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static readonly string[] AllNames =
    [
        nameof(AdjustmentSettings.Brightness), nameof(AdjustmentSettings.Contrast), nameof(AdjustmentSettings.Saturation),
        nameof(AdjustmentSettings.Warmth), nameof(AdjustmentSettings.Hue), nameof(AdjustmentSettings.Sharpen),
        nameof(AdjustmentSettings.Vignette), nameof(AdjustmentSettings.Grayscale), nameof(AdjustmentSettings.Sepia),
    ];

    private static readonly AdjustmentSettings AllMax = new()
    {
        Brightness = 100, Contrast = 100, Saturation = 100, Warmth = 100, Hue = 100,
        Sharpen = 100, Vignette = 100, Grayscale = 100, Sepia = 100,
    };

    private static readonly AdjustmentSettings AllMin = new()
    {
        Brightness = -100, Contrast = -100, Saturation = -100, Warmth = -100, Hue = -100,
    };

    // ---- OP-E24 crop ----

    [Theory]
    [InlineData(CropAspect.Free, 640, 480, 0, 0, 640, 480)]
    [InlineData(CropAspect.Square, 640, 480, 80, 0, 480, 480)]
    [InlineData(CropAspect.FourThree, 640, 480, 0, 0, 640, 480)]
    [InlineData(CropAspect.SixteenNine, 640, 480, 0, 60, 640, 360)]
    [InlineData(CropAspect.NineSixteen, 640, 480, 185, 0, 270, 480)]
    [InlineData(CropAspect.FourFive, 640, 480, 128, 0, 384, 480)]
    [InlineData(CropAspect.SixteenNine, 500, 700, 2, 210, 496, 279)]
    [InlineData(CropAspect.NineSixteen, 500, 700, 56, 6, 387, 688)]
    [InlineData(CropAspect.FourThree, 500, 700, 0, 162, 500, 375)]
    public void OP_E24_FitAspect_Exact_Size(CropAspect aspect, int imgW, int imgH, int x, int y, int w, int h)
    {
        Assert.Equal(new Int32Rect(x, y, w, h), Crop.FitAspect(imgW, imgH, aspect));
    }

    [Fact]
    public void OP_E24_FitAspect_Is_Exact_Largest_And_Centred_For_Every_Aspect()
    {
        foreach (var (imgW, imgH) in new[] { (640, 480), (500, 700), (333, 211), (1920, 1080), (17, 1000) })
        {
            foreach (CropAspect aspect in Enum.GetValues<CropAspect>().Where(a => a != CropAspect.Free))
            {
                var (aw, ah) = Crop.Ratio(aspect);
                Int32Rect r = Crop.FitAspect(imgW, imgH, aspect);
                Assert.Equal((long)r.Width * ah, (long)r.Height * aw);
                Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= imgW && r.Y + r.Height <= imgH, $"{aspect} {r} outside {imgW}x{imgH}");
                Assert.True(r.Width + aw > imgW || r.Height + ah > imgH, $"{aspect} {r} is not the largest in {imgW}x{imgH}");
                Assert.Equal((imgW - r.Width) / 2, r.X);
                Assert.Equal((imgH - r.Height) / 2, r.Y);
            }
        }
        // Smaller than one ratio unit: still inside and non-empty.
        Int32Rect tiny = Crop.FitAspect(5, 3, CropAspect.SixteenNine);
        Assert.True(tiny.Width >= 1 && tiny.Height >= 1 && tiny.Width <= 5 && tiny.Height <= 3);
    }

    [Fact]
    public void OP_E24_Cropped_Pixels_Equal_Source_Region()
    {
        RasterImage source = Raster(Img.Palette(64, 48));
        var rects = Enum.GetValues<CropAspect>().Select(a => Crop.FitAspect(64, 48, a)).Append(new Int32Rect(5, 7, 20, 11)).Append(new Int32Rect(63, 47, 1, 1));
        foreach (Int32Rect r in rects)
        {
            RasterImage cropped = Crop.Apply(source, r);
            Assert.Equal(r.Width, cropped.Width);
            Assert.Equal(r.Height, cropped.Height);
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    int s = ((r.Y + y) * source.Width + r.X + x) * 4, d = (y * r.Width + x) * 4;
                    Assert.True(source.Pixels.AsSpan(s, 4).SequenceEqual(cropped.Pixels.AsSpan(d, 4)), $"{r} pixel {x},{y}");
                }
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Crop.Apply(source, new Int32Rect(60, 0, 10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crop.Apply(source, new Int32Rect(0, 0, 0, 10)));
    }

    [Fact]
    public void OP_E24_Constrain_Clamps_To_Bounds_And_Aspect()
    {
        Assert.Equal(new Int32Rect(0, 0, 640, 480), Crop.Constrain(new Int32Rect(-50, -30, 2000, 2000), CropAspect.Free, 640, 480));
        Assert.Equal(new Int32Rect(600, 400, 40, 80), Crop.Constrain(new Int32Rect(600, 400, 100, 100), CropAspect.Free, 640, 480));
        Assert.Equal(new Int32Rect(639, 479, 1, 1), Crop.Constrain(new Int32Rect(700, 500, 10, 10), CropAspect.Free, 640, 480));
        Assert.Equal(new Int32Rect(80, 0, 480, 480), Crop.Constrain(new Int32Rect(-50, -30, 2000, 2000), CropAspect.Square, 640, 480));
        Assert.Equal(new Int32Rect(20, 22, 80, 45), Crop.Constrain(new Int32Rect(10, 20, 100, 50), CropAspect.SixteenNine, 640, 480));
        Assert.Equal(new Int32Rect(100, 100, 16, 9), Crop.Constrain(new Int32Rect(100, 100, 3, 3), CropAspect.SixteenNine, 640, 480));
        Assert.Equal(new Int32Rect(624, 471, 16, 9), Crop.Constrain(new Int32Rect(639, 479, 3, 3), CropAspect.SixteenNine, 640, 480));

        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var requested = new Int32Rect(random.Next(-300, 900), random.Next(-300, 700), random.Next(0, 1200), random.Next(0, 1000));
            foreach (CropAspect aspect in Enum.GetValues<CropAspect>())
            {
                Int32Rect r = Crop.Constrain(requested, aspect, 640, 480);
                Assert.True(r.Width >= 1 && r.Height >= 1, $"{requested} {aspect} -> {r} empty");
                Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 640 && r.Y + r.Height <= 480, $"{requested} {aspect} -> {r} outside");
                if (aspect != CropAspect.Free)
                {
                    var (aw, ah) = Crop.Ratio(aspect);
                    Assert.Equal((long)r.Width * ah, (long)r.Height * aw);
                }
            }
        }
    }

    [Fact]
    public void OP_E24_Rotate_Matches_Shared_Transform()
    {
        RasterImage source = Translucent(7, 5);
        foreach (RotateFlip op in Enum.GetValues<RotateFlip>())
        {
            RasterImage rotated = Crop.Rotate(source, op);
            Img.AssertPixelsEqual(ImageTransforms.Rotate(source.ToBitmap(), op), rotated.ToBitmap());
        }
    }

    // ---- OP-E25 identity ----

    [Fact]
    public void OP_E25_Identity_Settings_Return_Identical_Bytes()
    {
        RasterImage source = Translucent(97, 61);
        Assert.True(new AdjustmentSettings().IsIdentity);
        RasterImage result = Adjustments.Apply(source, new AdjustmentSettings());
        Assert.NotSame(source.Pixels, result.Pixels);
        Assert.Equal((source.Width, source.Height), (result.Width, result.Height));
        Assert.True(source.Pixels.AsSpan().SequenceEqual(result.Pixels), "identity changed pixels");
        Assert.True(source.Pixels.AsSpan().SequenceEqual(Adjustments.ApplyPreview(source, AdjustmentSettings.Identity, 200).Pixels));
        foreach (string name in AllNames)
            Assert.False(Setting(name, 1).IsIdentity, name);
    }

    // ---- OP-E26 direction and range ----

    [Fact]
    public void OP_E26_Brightness_Moves_Mean_Luminance_Monotonically()
    {
        RasterImage source = Raster(Img.Photo(120, 80));
        double previous = double.NegativeInfinity;
        foreach (int b in new[] { -100, -50, 0, 50, 100 })
        {
            double mean = MeanLuma(Adjustments.Apply(source, new AdjustmentSettings { Brightness = b }));
            Assert.True(mean > previous, $"brightness {b}: {mean} <= {previous}");
            previous = mean;
        }
        double original = MeanLuma(source);
        Assert.True(MeanLuma(Adjustments.Apply(source, new AdjustmentSettings { Brightness = 50 })) > original);
        Assert.True(MeanLuma(Adjustments.Apply(source, new AdjustmentSettings { Brightness = -50 })) < original);
    }

    [Fact]
    public void OP_E26_Grayscale_100_Gives_Equal_Channels()
    {
        RasterImage source = Translucent(80, 60);
        var settings = new[]
        {
            new AdjustmentSettings { Grayscale = 100 },
            AllMax,
            new AdjustmentSettings { Grayscale = 100, Brightness = 20, Contrast = 30, Saturation = 80, Warmth = 60, Hue = 40, Sepia = 50, Sharpen = 60, Vignette = 70 },
        };
        foreach (AdjustmentSettings s in settings)
        {
            byte[] p = Adjustments.Apply(source, s).Pixels;
            for (int i = 0; i < p.Length; i += 4)
                Assert.True(p[i] == p[i + 1] && p[i + 1] == p[i + 2], $"{s}: pixel {i / 4} = {p[i]},{p[i + 1]},{p[i + 2]}");
        }
    }

    [Fact]
    public void OP_E26_Alpha_Unchanged_And_Extremes_Saturate()
    {
        RasterImage source = Translucent(90, 70);
        var all = AllNames.SelectMany(n => n is nameof(AdjustmentSettings.Sharpen) or nameof(AdjustmentSettings.Vignette)
                or nameof(AdjustmentSettings.Grayscale) or nameof(AdjustmentSettings.Sepia)
                ? new[] { Setting(n, 100) }
                : new[] { Setting(n, -100), Setting(n, 100) })
            .Append(AllMax).Append(AllMin);
        foreach (AdjustmentSettings s in all)
        {
            RasterImage result = Adjustments.Apply(source, s);
            for (int i = 3; i < source.Pixels.Length; i += 4)
                Assert.True(source.Pixels[i] == result.Pixels[i], $"{s}: alpha changed at pixel {i / 4}");
            Adjustments.Apply(Raster(Img.Noise(40, 30)), s);
        }

        // Saturation at the ends instead of wrap-around.
        Assert.All(Adjustments.Apply(Solid(8, 8, 255), new AdjustmentSettings { Brightness = 100 }).Pixels, v => Assert.Equal((byte)255, v));
        Assert.All(Adjustments.Apply(Solid(8, 8, 0), new AdjustmentSettings { Brightness = -100 }).Pixels.Where((_, i) => i % 4 != 3), v => Assert.Equal((byte)0, v));
        Assert.All(Adjustments.Apply(Solid(8, 8, 250), new AdjustmentSettings { Contrast = 100 }).Pixels, v => Assert.Equal((byte)255, v));
        Assert.All(Adjustments.Apply(Solid(8, 8, 5), new AdjustmentSettings { Contrast = 100 }).Pixels.Where((_, i) => i % 4 != 3), v => Assert.Equal((byte)0, v));
        Assert.All(Adjustments.Apply(Solid(8, 8, 250), new AdjustmentSettings { Warmth = 100 }).Pixels.Where((_, i) => i % 4 == 2), v => Assert.Equal((byte)255, v));

        Assert.Throws<ArgumentOutOfRangeException>(() => Adjustments.Apply(source, new AdjustmentSettings { Brightness = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Adjustments.Apply(source, new AdjustmentSettings { Sharpen = -1 }));
    }

    [Fact]
    public void OP_E26_Presets_Apply_And_Original_Is_Identity()
    {
        Assert.Equal(new[] { "original", "vivid", "warm", "cool", "faded", "dramatic", "soft", "mono" }, Adjustments.Presets.Select(p => p.Key));
        RasterImage source = Translucent(64, 48);
        foreach (var (key, settings) in Adjustments.Presets)
        {
            settings.Validate();
            RasterImage result = Adjustments.Apply(source, settings);
            if (key == "original")
            {
                Assert.True(settings.IsIdentity);
                Assert.True(source.Pixels.AsSpan().SequenceEqual(result.Pixels), "original preset changed pixels");
            }
            else
            {
                Assert.False(settings.IsIdentity, key);
                Assert.False(source.Pixels.AsSpan().SequenceEqual(result.Pixels), $"{key} preset changed nothing");
            }
        }
    }

    // ---- OP-E27 preview vs full ----

    private static readonly Lazy<RasterImage> PreviewSource = new(() => Raster(Img.Photo(1200, 900)));

    private static readonly Lazy<RasterImage> PreviewNoise = new(() => Raster(Img.Noise(1200, 900)));

    private const int PreviewSide = 300;

    private static double PreviewPsnr(AdjustmentSettings s, RasterImage? source = null)
    {
        source ??= PreviewSource.Value;
        BitmapSource full = Adjustments.Apply(source, s).ToBitmap();
        BitmapSource fullDown = ImageTransforms.Resize(full, new ResizeOptions(ResizeMode.LongAxis, LongAxis: PreviewSide));
        RasterImage preview = Adjustments.ApplyPreview(source, s, PreviewSide);
        Assert.Equal((fullDown.PixelWidth, fullDown.PixelHeight), (preview.Width, preview.Height));
        Assert.Equal(PreviewSide, Math.Max(preview.Width, preview.Height));
        return Img.Psnr(fullDown, preview.ToBitmap());
    }

    [Theory]
    [InlineData("Brightness", 40)]
    [InlineData("Brightness", -60)]
    [InlineData("Contrast", 50)]
    [InlineData("Contrast", -50)]
    [InlineData("Saturation", 70)]
    [InlineData("Saturation", -100)]
    [InlineData("Warmth", 60)]
    [InlineData("Warmth", -60)]
    [InlineData("Hue", 50)]
    [InlineData("Vignette", 100)]
    [InlineData("Grayscale", 100)]
    [InlineData("Sepia", 80)]
    public void OP_E27_Preview_Matches_Downscaled_Full_Result(string name, int value)
    {
        double psnr = PreviewPsnr(Setting(name, value));
        Assert.True(psnr >= 40, $"{name} {value}: PSNR {psnr:F2} dB < 40");
    }

    [Fact]
    public void OP_E27_Preview_Matches_For_Combined_Settings_Without_Sharpen()
    {
        double psnr = PreviewPsnr(new AdjustmentSettings
        {
            Brightness = 10, Contrast = 25, Saturation = 30, Warmth = 20, Hue = 10, Vignette = 40, Sepia = 20,
        });
        Assert.True(psnr >= 40, $"combined: PSNR {psnr:F2} dB < 40");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void OP_E27_Preview_With_Sharpen_Matches_On_Photo(int sharpen)
    {
        // Measured 2026-11-17: 20 → identical, 50 → 73.95 dB, 100 → 59.87 dB.
        double psnr = PreviewPsnr(new AdjustmentSettings { Sharpen = sharpen });
        Assert.True(psnr >= 40, $"sharpen {sharpen}: PSNR {psnr:F2} dB < 40");
    }

    /// <summary>
    /// Worst case for a scale-dependent filter: per-pixel noise has all its detail above the preview's resolution, so
    /// sharpening cannot match the downscaled full result. Floors are the measured values minus about 2.5 dB
    /// (measured 2026-11-17: 20 → 42.06, 50 → 34.10, 100 → 28.76 dB); the 40 dB floor above still holds for photos.
    /// </summary>
    [Theory]
    [InlineData(20, 39.5)]
    [InlineData(50, 31.5)]
    [InlineData(100, 26.0)]
    public void OP_E27_Preview_With_Sharpen_On_Noise_Stays_Above_Measured_Floor(int sharpen, double floor)
    {
        double psnr = PreviewPsnr(new AdjustmentSettings { Sharpen = sharpen }, PreviewNoise.Value);
        Assert.True(psnr >= floor, $"sharpen {sharpen} on noise: PSNR {psnr:F2} dB < {floor}");
    }

    // ---- OP-E28 undo / redo ----

    private static (int W, int H, byte[] Pixels) Snapshot(EditSession s)
    {
        RasterImage r = s.Render();
        return (r.Width, r.Height, r.Pixels);
    }

    private static void AssertSnapshot((int W, int H, byte[] Pixels) expected, EditSession s, string label)
    {
        var actual = Snapshot(s);
        Assert.Equal((expected.W, expected.H), (actual.W, actual.H));
        Assert.True(expected.Pixels.AsSpan().SequenceEqual(actual.Pixels), label);
    }

    [Fact]
    public void OP_E28_Undo_Redo_Restores_Every_State()
    {
        RasterImage original = Raster(Img.Palette(64, 48));
        byte[] originalBytes = (byte[])original.Pixels.Clone();
        var session = new EditSession(original);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);

        Action[] ops =
        [
            () => session.SetAdjustments(new AdjustmentSettings { Brightness = 20 }),
            () => session.Crop(new Int32Rect(4, 6, 50, 30)),
            () => session.Rotate(RotateFlip.Rotate90),
            () => session.SetAdjustments(new AdjustmentSettings { Brightness = 20, Saturation = 40, Vignette = 30 }),
            () => session.Rotate(RotateFlip.FlipHorizontal),
            () => session.Crop(Crop.FitAspect(session.Base.Width, session.Base.Height, CropAspect.Square)),
            () => session.SetAdjustments(Adjustments.Presets[^1].Settings),
        ];
        var states = new List<(int W, int H, byte[] Pixels)> { Snapshot(session) };
        foreach (Action op in ops)
        {
            op();
            states.Add(Snapshot(session));
        }
        Assert.Equal(ops.Length, session.UndoCount);

        for (int i = ops.Length; i > 0; i--)
        {
            Assert.True(session.CanUndo);
            session.Undo();
            AssertSnapshot(states[i - 1], session, $"after undo to state {i - 1}");
        }
        Assert.False(session.CanUndo);
        Assert.True(originalBytes.AsSpan().SequenceEqual(session.Base.Pixels), "base not restored");
        Assert.True(originalBytes.AsSpan().SequenceEqual(session.Render().Pixels), "render not restored");
        Assert.True(session.Settings.IsIdentity);
        Assert.Throws<InvalidOperationException>(() => session.Undo());

        for (int i = 1; i <= ops.Length; i++)
        {
            Assert.True(session.CanRedo);
            session.Redo();
            AssertSnapshot(states[i], session, $"after redo to state {i}");
        }
        Assert.False(session.CanRedo);
        Assert.Equal(0, session.DroppedHistory);

        // A new step after an undo discards the redo branch.
        session.Undo();
        session.Rotate(RotateFlip.Rotate180);
        Assert.False(session.CanRedo);
        Assert.True(originalBytes.AsSpan().SequenceEqual(original.Pixels), "source image was modified in place");
    }

    [Fact]
    public void OP_E28_History_Budget_Drops_Oldest_Steps()
    {
        RasterImage image = Raster(Img.Palette(32, 32));
        var session = new EditSession(image, historyBudgetBytes: 2 * image.ByteSize);
        for (int i = 0; i < 5; i++)
            session.Rotate(RotateFlip.Rotate90);
        Assert.Equal(3, session.DroppedHistory);
        Assert.Equal(2, session.UndoCount);
        Assert.True(session.HistoryBytes <= session.HistoryBudgetBytes);

        session.Undo();
        session.Undo();
        Assert.False(session.CanUndo);
        // The oldest reachable state is after the third rotation.
        Assert.True(Crop.Rotate(image, RotateFlip.Rotate270).Pixels.AsSpan().SequenceEqual(session.Base.Pixels));

        // Adjustment steps hold no pixels, so even a zero budget keeps them.
        var adjustOnly = new EditSession(image, historyBudgetBytes: 0);
        for (int i = 1; i <= 10; i++)
            adjustOnly.SetAdjustments(new AdjustmentSettings { Brightness = i });
        Assert.Equal(10, adjustOnly.UndoCount);
        Assert.Equal(0, adjustOnly.DroppedHistory);
        Assert.Equal(0, adjustOnly.HistoryBytes);
    }

    [Fact]
    public void OP_E28_CanEdit_Applies_Pixel_And_Memory_Limits()
    {
        Assert.False(EditSession.CanEdit(20000, 20000, out long huge));
        Assert.Equal(20000L * 20000 * 4 * 3, huge);
        Assert.True(EditSession.CanEdit(4000, 3000, out long photo));
        Assert.Equal(4000L * 3000 * 12, photo);
        // Within the 200 M pixel loader limit but over the 1.5 GB editing estimate.
        Assert.False(EditSession.CanEdit(15000, 10000, out _));
        Assert.False(EditSession.CanEdit(0, 10, out _));
    }

    // ---- OP-E29 export ----

    [Fact]
    public void OP_E29_Png_Round_Trip_Is_Lossless()
    {
        foreach (RasterImage image in new[] { Raster(Img.Photo(64, 48)), Translucent(50, 30) })
        {
            BitmapSource decoded = DecodeBytes(EditExport.Encode(image, ExportFormat.Png));
            Img.AssertPixelsEqual(image.ToBitmap(), decoded);
        }
    }

    [Fact]
    public void OP_E29_Jpeg_Size_Grows_With_Quality_And_Flattens_Alpha()
    {
        RasterImage noise = Raster(Img.Noise(128, 96));
        long q30 = EditExport.Estimate(noise, ExportFormat.Jpeg, 30).Bytes;
        long q90 = EditExport.Estimate(noise, ExportFormat.Jpeg, 90).Bytes;
        Assert.True(q30 < q90, $"q30 {q30} >= q90 {q90}");

        RasterImage clear = Raster(Img.Create(16, 16, (_, _) => (0, 0, 0, 0)));
        byte[] flat = Img.Bgra(DecodeBytes(EditExport.Encode(clear, ExportFormat.Jpeg, 95)));
        Assert.All(flat, v => Assert.True(v >= 250, $"value {v}"));
    }

    [Theory]
    [InlineData(0.5, 100, 50)]
    [InlineData(1, 200, 100)]
    [InlineData(2, 400, 200)]
    [InlineData(3, 600, 300)]
    public void OP_E29_Scale_Gives_Exact_Dimensions(double scale, int width, int height)
    {
        RasterImage image = Raster(Img.Photo(200, 100));
        foreach (ExportFormat format in Enum.GetValues<ExportFormat>())
        {
            var estimate = EditExport.Estimate(image, format, 85, scale);
            Assert.Equal((width, height), (estimate.Width, estimate.Height));
            BitmapSource decoded = DecodeBytes(EditExport.Encode(image, format, 85, scale));
            Assert.Equal((width, height), (decoded.PixelWidth, decoded.PixelHeight));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => EditExport.Encode(image, ExportFormat.Png, 85, 0));
    }

    [Fact]
    public void OP_E29_Save_Matches_Estimate_And_Leaves_No_Temp_Files()
    {
        RasterImage image = Translucent(120, 90);
        foreach (ExportFormat format in Enum.GetValues<ExportFormat>())
        {
            foreach (double scale in new[] { 0.5, 1.0, 2.0 })
            {
                string path = Temp.Combine($"out_{format}_{scale}.{(format == ExportFormat.Png ? "png" : "jpg")}");
                var estimate = EditExport.Estimate(image, format, 80, scale);
                EditExport.Save(path, image, format, 80, scale);
                Assert.Equal(estimate.Bytes, new FileInfo(path).Length);
                BitmapSource decoded = Img.Decode(path);
                Assert.Equal((estimate.Width, estimate.Height), (decoded.PixelWidth, decoded.PixelHeight));
                // Overwriting an existing export goes through the same atomic path.
                EditExport.Save(path, image, format, 80, scale);
                Assert.Equal(estimate.Bytes, new FileInfo(path).Length);
            }
        }
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }
}
