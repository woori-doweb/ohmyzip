using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core.Tests;

/// <summary>Read-only procedural fixtures (three 1280x800 PNGs) and their expected hashes.</summary>
internal static class Fixtures
{
    public static readonly IReadOnlyDictionary<string, string> ExpectedHashes = new Dictionary<string, string>
    {
        ["fixture-01.png"] = "8d0830697d4bdb5c915005782c9615e3d289740a12215c575050bc2335c1d602",
        ["fixture-02.png"] = "f2e984d3b5bdaf6417b241189ae590fb1985384b897fc5c0b1ea10f7d4af1e27",
        ["fixture-03.png"] = "7c0f5690c7f28a033369833955d224531960dca8dbadaa154ab8c421af892d9f",
    };

    public static readonly string[] Names = ["fixture-01.png", "fixture-02.png", "fixture-03.png"];

    private static readonly Lazy<string> Dir = new(Locate);

    public static string Directory => Dir.Value;

    private static string Locate()
    {
        string? env = Environment.GetEnvironmentVariable("OHMYPIC_FIXTURES");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
            return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "data", "ohmypic", "fixtures");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Fixtures not found. Set the OHMYPIC_FIXTURES environment variable.");
    }

    /// <summary>Copies a fixture into the temp folder and returns the copy's path. The original is never opened for writing.</summary>
    public static string CopyTo(string name, TempDir temp, string? subFolder = null)
    {
        string folder = subFolder is null ? temp.Path : temp.Combine(subFolder);
        System.IO.Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name);
        File.Copy(Path.Combine(Directory, name), target);
        return target;
    }

    public static void AssertOriginalsUnchanged()
    {
        foreach (var (name, hash) in ExpectedHashes)
            Assert.Equal(hash, Util.Sha256(Path.Combine(Directory, name)));
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohmypic-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Every test class gets a private temp folder; on dispose the fixture originals must still match their hashes.</summary>
public abstract class TestBase : IDisposable
{
    internal TempDir Temp { get; } = new();

    public void Dispose()
    {
        Temp.Dispose();
        Fixtures.AssertOriginalsUnchanged();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Progress&lt;T&gt; posts asynchronously; tests need the callback to run inline.</summary>
internal sealed class SyncProgress(Action<JobProgress> onReport) : IProgress<JobProgress>
{
    public void Report(JobProgress value) => onReport(value);
}

internal static class Util
{
    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Files in a folder (top level), names only, ordinal order.</summary>
    public static List<string> FileNames(string folder) =>
        Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToList();

    public static List<string> TempFilesUnder(string root) =>
        Directory.GetFiles(root, "*" + AtomicFile.TempSuffix, SearchOption.AllDirectories).ToList();
}

/// <summary>Synthetic images and pixel comparison.</summary>
internal static class Img
{
    public static BitmapSource Create(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                int i = (y * width + x) * 4;
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
                data[i + 3] = a;
            }
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Opaque image with at most 200 distinct colours and no symmetry (good for lossless and rotation checks).</summary>
    public static BitmapSource Palette(int width, int height) => Create(width, height, (x, y) =>
    {
        int idx = (x * 7 + y * 13 + x * y) % 200;
        return ((byte)(idx * 37 % 256), (byte)(idx * 91 % 256), (byte)(idx * 53 % 256), 255);
    });

    /// <summary>Smooth, photo-like opaque image.</summary>
    public static BitmapSource Photo(int width, int height) => Create(width, height, (x, y) =>
        ((byte)(128 + 60 * Math.Sin((x + y) / 31.0)),
         (byte)(128 + 100 * Math.Cos(y / 23.0)),
         (byte)(128 + 100 * Math.Sin(x / 17.0)),
         255));

    /// <summary>Incompressible noise (deterministic).</summary>
    public static BitmapSource Noise(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        return Create(width, height, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255));
    }

    public static byte[] Bgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static (byte B, byte G, byte R, byte A) At(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }

    public static void AssertPixelsEqual(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.True(Bgra(expected).AsSpan().SequenceEqual(Bgra(actual)), "BGRA32 pixels differ");
    }

    /// <summary>Largest absolute difference of any B/G/R/A channel.</summary>
    public static int MaxChannelDiff(BitmapSource a, BitmapSource b)
    {
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        byte[] pa = Bgra(a), pb = Bgra(b);
        int max = 0;
        for (int i = 0; i < pa.Length; i++)
            max = Math.Max(max, Math.Abs(pa[i] - pb[i]));
        return max;
    }

    /// <summary>PSNR over the colour channels (alpha ignored). Identical images give +∞.</summary>
    public static double Psnr(BitmapSource reference, BitmapSource test)
    {
        Assert.Equal(reference.PixelWidth, test.PixelWidth);
        Assert.Equal(reference.PixelHeight, test.PixelHeight);
        byte[] a = Bgra(reference), b = Bgra(test);
        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                double d = a[i + c] - b[i + c];
                sum += d * d;
                n++;
            }
        }
        double mse = sum / n;
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Decodes a file fully into memory (no lock, no colour conversion).</summary>
    public static BitmapSource Decode(string path, int frame = 0)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapFrame result = decoder.Frames[frame];
        result.Freeze();
        return result;
    }

    public static int FrameCount(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Count;
    }

    public const string CameraModel = "OhMyPic Test Camera";
    public const string DateTakenExif = "2024:05:06 07:08:09";
    public const string DateTakenDisplay = "2024-05-06 07:08:09";

    /// <summary>JPEG with EXIF Make/Model/Orientation/DateTimeOriginal written through BitmapMetadata.</summary>
    public static void WriteJpegWithExif(string path, BitmapSource image, int orientation = 1, int quality = 92, bool gray = false, string? description = null)
    {
        var metadata = new BitmapMetadata("jpg");
        if (description is not null)
            metadata.SetQuery("/app1/ifd/{ushort=270}", description);
        metadata.SetQuery("/app1/ifd/{ushort=271}", "TestMake");
        metadata.SetQuery("/app1/ifd/{ushort=272}", CameraModel);
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", DateTakenExif);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, gray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void WritePng(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Animated GIF with three full-size frames: red, green, blue.</summary>
    public static void WriteThreeFrameGif(string path, int width = 40, int height = 30)
    {
        var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue };
        var encoder = new GifBitmapEncoder();
        foreach (Color c in colors)
        {
            var palette = new BitmapPalette([c, Colors.White]);
            byte[] indices = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    indices[y * width + x] = (byte)(x < 4 && y < 4 ? 1 : 0);
            var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, palette, indices, width);
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Valid PNG header claiming 20000x20000 (400 M pixels) with almost no pixel data.</summary>
    public static void WriteHugePngHeader(string path)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, 20000);
        WriteBigEndian(ihdr, 4, 20000);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolour
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WriteChunk(ms, "IEND", []);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteBigEndian(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] header = new byte[4];
        WriteBigEndian(header, 0, (uint)data.Length);
        stream.Write(header);
        byte[] typeAndData = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typeAndData);
        byte[] crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
