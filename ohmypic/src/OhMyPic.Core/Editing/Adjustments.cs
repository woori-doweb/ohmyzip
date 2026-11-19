namespace OhMyPic.Core.Editing;

/// <summary>
/// Slider values of the adjustment panel. Brightness, Contrast, Saturation, Warmth and Hue range -100..100; Sharpen,
/// Vignette, Grayscale and Sepia range 0..100. All zero is the identity.
/// </summary>
public sealed record AdjustmentSettings
{
    public const int SignedMin = -100;
    public const int Max = 100;

    public static AdjustmentSettings Identity { get; } = new();

    public int Brightness { get; init; }

    public int Contrast { get; init; }

    public int Saturation { get; init; }

    public int Warmth { get; init; }

    public int Hue { get; init; }

    public int Sharpen { get; init; }

    public int Vignette { get; init; }

    public int Grayscale { get; init; }

    public int Sepia { get; init; }

    public bool IsIdentity =>
        Brightness == 0 && Contrast == 0 && Saturation == 0 && Warmth == 0 && Hue == 0
        && Sharpen == 0 && Vignette == 0 && Grayscale == 0 && Sepia == 0;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when any value is outside its range.</summary>
    public void Validate()
    {
        Check(Brightness, SignedMin, nameof(Brightness));
        Check(Contrast, SignedMin, nameof(Contrast));
        Check(Saturation, SignedMin, nameof(Saturation));
        Check(Warmth, SignedMin, nameof(Warmth));
        Check(Hue, SignedMin, nameof(Hue));
        Check(Sharpen, 0, nameof(Sharpen));
        Check(Vignette, 0, nameof(Vignette));
        Check(Grayscale, 0, nameof(Grayscale));
        Check(Sepia, 0, nameof(Sepia));
    }

    private static void Check(int value, int min, string name)
    {
        if (value < min || value > Max)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be within {min}..{Max}.");
    }
}

/// <summary>
/// Per-pixel colour adjustments on straight-alpha BGRA32. Order: sharpen (unsharp mask) → brightness/contrast →
/// warmth → colour matrix (saturation, hue, sepia, grayscale) → vignette. Alpha is never changed.
/// </summary>
public static class Adjustments
{
    /// <summary>Brightness ±100 shifts every channel by up to this many levels.</summary>
    private const float BrightnessLevels = 100f;

    /// <summary>Warmth ±100 adds/subtracts up to this many levels to red and the opposite to blue.</summary>
    private const float WarmthLevels = 40f;

    /// <summary>Unsharp-mask gain at Sharpen 100.</summary>
    private const float MaxSharpenAmount = 1.5f;

    /// <summary>Darkening in the image corners at Vignette 100.</summary>
    private const float MaxVignette = 0.8f;

    private const float LumaR = 0.299f, LumaG = 0.587f, LumaB = 0.114f;

    /// <summary>Built-in looks (own values). The first entry is the identity.</summary>
    public static IReadOnlyList<(string Key, AdjustmentSettings Settings)> Presets { get; } =
    [
        ("original", AdjustmentSettings.Identity),
        ("vivid", new AdjustmentSettings { Contrast = 15, Saturation = 35, Sharpen = 20 }),
        ("warm", new AdjustmentSettings { Brightness = 5, Saturation = 10, Warmth = 35 }),
        ("cool", new AdjustmentSettings { Contrast = 5, Warmth = -35 }),
        ("faded", new AdjustmentSettings { Brightness = 10, Contrast = -30, Saturation = -30 }),
        ("dramatic", new AdjustmentSettings { Contrast = 45, Saturation = -15, Sharpen = 25, Vignette = 50 }),
        ("soft", new AdjustmentSettings { Brightness = 8, Contrast = -15, Saturation = -10 }),
        ("mono", new AdjustmentSettings { Contrast = 15, Grayscale = 100 }),
    ];

    /// <summary>Applies the settings to a new image. The identity returns a byte-identical copy.</summary>
    public static RasterImage Apply(RasterImage source, AdjustmentSettings settings) => Apply(source, settings, 1f);

    private static RasterImage Apply(RasterImage source, AdjustmentSettings settings, float sharpenScale)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.IsIdentity)
            return source.Clone();

        byte[] input = settings.Sharpen > 0 ? UnsharpMask(source, settings.Sharpen * MaxSharpenAmount / 100f * sharpenScale) : source.Pixels;
        byte[] alpha = source.Pixels;
        float[] tone = ToneCurve(settings.Brightness, settings.Contrast);
        float warm = settings.Warmth * WarmthLevels / 100f;
        bool useMatrix = settings.Saturation != 0 || settings.Hue != 0 || settings.Sepia != 0 || settings.Grayscale != 0;
        float[] m = ColorMatrix(settings);
        float vignette = settings.Vignette * MaxVignette / 100f;
        int width = source.Width, height = source.Height;
        byte[] output = new byte[input.Length];

        Parallel.For(0, height, y =>
        {
            float ny = (y + 0.5f) / height * 2f - 1f;
            int i = y * width * 4;
            for (int x = 0; x < width; x++, i += 4)
            {
                float b = tone[input[i]], g = tone[input[i + 1]], r = tone[input[i + 2]];
                if (warm != 0)
                {
                    r = Math.Clamp(r + warm, 0f, 255f);
                    b = Math.Clamp(b - warm, 0f, 255f);
                }
                if (useMatrix)
                {
                    float r2 = m[0] * r + m[1] * g + m[2] * b;
                    float g2 = m[3] * r + m[4] * g + m[5] * b;
                    float b2 = m[6] * r + m[7] * g + m[8] * b;
                    (r, g, b) = (r2, g2, b2);
                }
                if (vignette > 0)
                {
                    float nx = (x + 0.5f) / width * 2f - 1f;
                    float factor = 1f - vignette * (nx * nx + ny * ny) * 0.5f;
                    r *= factor;
                    g *= factor;
                    b *= factor;
                }
                output[i] = ToByte(b);
                output[i + 1] = ToByte(g);
                output[i + 2] = ToByte(r);
                output[i + 3] = alpha[i + 3];
            }
        });
        return new RasterImage(width, height, output);
    }

    /// <summary>
    /// Fast preview: scales the image so its longer side is at most <paramref name="maxSide"/> (high-quality resize,
    /// never enlarges), then applies the settings with the sharpen gain scaled by the same factor.
    /// </summary>
    public static RasterImage ApplyPreview(RasterImage source, AdjustmentSettings settings, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        if (Math.Max(source.Width, source.Height) <= maxSide)
            return Apply(source, settings);
        var small = ImageTransforms.Resize(source.ToBitmap(), new ResizeOptions(ResizeMode.LongAxis, LongAxis: maxSide));
        // A 3x3 kernel on the preview covers more of the photo than on the full image; scaling the gain by the
        // downscale factor makes the preview match the full result viewed at the preview size.
        float scale = maxSide / (float)Math.Max(source.Width, source.Height);
        return Apply(RasterImage.FromBitmap(small), settings, scale);
    }

    /// <summary>Brightness (additive) then contrast (around mid-grey), clamped to 0..255. Zero/zero is the identity.</summary>
    private static float[] ToneCurve(int brightness, int contrast)
    {
        float offset = brightness * BrightnessLevels / 100f;
        // -100 flattens to mid-grey, +100 triples the slope.
        float slope = contrast >= 0 ? 1f + contrast * 2f / 100f : 1f + contrast / 100f;
        var curve = new float[256];
        for (int v = 0; v < 256; v++)
        {
            float x = v + offset;
            if (contrast != 0)
                x = (x - 127.5f) * slope + 127.5f;
            curve[v] = Math.Clamp(x, 0f, 255f);
        }
        return curve;
    }

    /// <summary>Row-major 3x3 RGB matrix: grayscale · sepia · hue · saturation (applied right to left).</summary>
    private static float[] ColorMatrix(AdjustmentSettings s)
    {
        float[] luma = [LumaR, LumaG, LumaB, LumaR, LumaG, LumaB, LumaR, LumaG, LumaB];
        float[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

        float f = 1f + s.Saturation / 100f;
        float[] saturation = Lerp(luma, identity, f);

        double angle = s.Hue / 100.0 * Math.PI;
        float c = (float)Math.Cos(angle), n = (float)Math.Sin(angle);
        // Luminance-preserving hue rotation.
        float[] hue =
        [
            0.299f + 0.701f * c + 0.168f * n, 0.587f - 0.587f * c + 0.330f * n, 0.114f - 0.114f * c - 0.497f * n,
            0.299f - 0.299f * c - 0.328f * n, 0.587f + 0.413f * c + 0.035f * n, 0.114f - 0.114f * c + 0.292f * n,
            0.299f - 0.300f * c + 1.250f * n, 0.587f - 0.588f * c - 1.050f * n, 0.114f + 0.886f * c - 0.203f * n,
        ];

        float[] sepiaTone = [0.393f, 0.769f, 0.189f, 0.349f, 0.686f, 0.168f, 0.272f, 0.534f, 0.131f];
        float[] sepia = Lerp(identity, sepiaTone, s.Sepia / 100f);
        // Grayscale is applied last so 100 gives identical rows, hence R = G = B exactly.
        float[] gray = Lerp(identity, luma, s.Grayscale / 100f);

        float[] m = s.Saturation != 0 ? saturation : identity;
        if (s.Hue != 0)
            m = Multiply(hue, m);
        if (s.Sepia != 0)
            m = Multiply(sepia, m);
        if (s.Grayscale != 0)
            m = Multiply(gray, m);
        return m;
    }

    /// <summary>(1 - t)·a + t·b element-wise.</summary>
    private static float[] Lerp(float[] a, float[] b, float t)
    {
        var r = new float[9];
        for (int i = 0; i < 9; i++)
            r[i] = (1f - t) * a[i] + t * b[i];
        return r;
    }

    private static float[] Multiply(float[] l, float[] r)
    {
        var m = new float[9];
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                m[row * 3 + col] = l[row * 3] * r[col] + l[row * 3 + 1] * r[3 + col] + l[row * 3 + 2] * r[6 + col];
        return m;
    }

    /// <summary>v + amount · (v − blur) per colour channel with a 3x3 Gaussian (1-2-1) blur; edges repeat; alpha copied.</summary>
    private static byte[] UnsharpMask(RasterImage source, float amount)
    {
        int width = source.Width, height = source.Height;
        byte[] src = source.Pixels;
        byte[] dst = new byte[src.Length];
        Parallel.For(0, height, y =>
        {
            int up = Math.Max(0, y - 1) * width, mid = y * width, down = Math.Min(height - 1, y + 1) * width;
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - 1), right = Math.Min(width - 1, x + 1);
                int i = (mid + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum =
                        src[(up + left) * 4 + c] + 2 * src[(up + x) * 4 + c] + src[(up + right) * 4 + c]
                        + 2 * src[(mid + left) * 4 + c] + 4 * src[i + c] + 2 * src[(mid + right) * 4 + c]
                        + src[(down + left) * 4 + c] + 2 * src[(down + x) * 4 + c] + src[(down + right) * 4 + c];
                    float v = src[i + c];
                    dst[i + c] = ToByte(v + amount * (v - sum / 16f));
                }
                dst[i + 3] = src[i + 3];
            }
        });
        return dst;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v), 0f, 255f);
}
