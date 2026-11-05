using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>
/// Runs an <see cref="EditJob"/>: resize → rotate → rename → convert, one input at a time.
/// Per-item problems never throw; they come back as <see cref="ItemStatus.Failed"/> with an <see cref="EditErrorCode"/>.
/// </summary>
public static class EditPipeline
{
    private sealed record Encoded(byte[] Bytes, int Width, int Height, int FrameCount);

    private sealed record PlannedOutput(string FileName, Func<Encoded> Encode);

    public static JobResult Run(EditJob job, Func<ConflictInfo, ConflictDecision>? ask, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(job.Inputs);
        ArgumentNullException.ThrowIfNull(job.Output);
        int total = job.Inputs.Count;
        var results = new List<ItemResult>(total);
        // Outputs written by this job, so two inputs never silently land on the same file.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total; i++)
        {
            string input = job.Inputs[i];
            progress?.Report(new JobProgress(i, total, input));
            if (ct.IsCancellationRequested)
            {
                for (int k = i; k < total; k++)
                    results.Add(new ItemResult(job.Inputs[k], [], ItemStatus.Skipped, EditErrorCode.Cancelled, "Cancelled before processing.", false));
                break;
            }
            results.Add(ProcessItem(job, input, i, ask, reserved, ct));
        }
        progress?.Report(new JobProgress(results.Count(r => r.Error != EditErrorCode.Cancelled), total, ""));
        return new JobResult(results);
    }

    private static ItemResult ProcessItem(EditJob job, string input, int index, Func<ConflictInfo, ConflictDecision>? ask, HashSet<string> reserved, CancellationToken ct)
    {
        var written = new List<string>();
        bool lossless = false;
        try
        {
            string full = Path.GetFullPath(input);
            if (!File.Exists(full))
                return Failed(input, written, EditErrorCode.IoError, "The file does not exist.");
            if (!ImageFormats.IsReadable(full))
                return Failed(input, written, EditErrorCode.UnsupportedFormat, $"'{Path.GetExtension(full)}' is not a readable image format.");
            ConvertOptions? convert = job.Convert;
            if (convert is { Frames: FrameMode.CombineOne } && !ImageFormats.SupportsMultipleFrames(convert.Format))
                return Failed(input, written, EditErrorCode.UnsupportedFormat, $"{convert.Format} cannot hold multiple frames; use TIFF or GIF.");

            string folder;
            try
            {
                folder = OutputPlanner.ResolveFolder(full, job.Output);
            }
            catch (ArgumentException ex)
            {
                return Failed(input, written, EditErrorCode.IoError, ex.Message);
            }
            DateTime date = File.GetLastWriteTime(full);
            string extension = OutputPlanner.ResolveExtension(full, convert);
            ImageFormatKind kind = ImageFormats.FromExtension(extension)!.Value;

            List<PlannedOutput>? planned = TryPlanLosslessJpeg(job, full, index, date, extension);
            if (planned is not null)
                lossless = true;
            else
                planned = PlanReencode(job, full, index, date, extension, kind);

            int skipped = 0;
            foreach (PlannedOutput output in planned)
            {
                string target = Path.Combine(folder, output.FileName);
                if (File.Exists(target) || Directory.Exists(target) || reserved.Contains(target))
                {
                    ConflictDecision decision = job.Output.Conflict switch
                    {
                        ConflictPolicy.Overwrite => ConflictDecision.Overwrite,
                        ConflictPolicy.Ask => ask?.Invoke(new ConflictInfo(full, target)) ?? ConflictDecision.Skip,
                        _ => ConflictDecision.Skip,
                    };
                    if (decision == ConflictDecision.Skip || (decision == ConflictDecision.Overwrite && Directory.Exists(target)))
                    {
                        skipped++;
                        continue;
                    }
                    if (decision == ConflictDecision.Rename)
                        target = OutputPlanner.MakeUnique(target, reserved);
                }
                Directory.CreateDirectory(folder);
                Encoded encoded = ImageEncoder.GuardEncode(output.Encode);
                AtomicFile.WriteAllBytes(target, encoded.Bytes, temp => Verify(temp, encoded), ct);
                reserved.Add(target);
                written.Add(target);
            }

            if (written.Count == 0)
                return new ItemResult(input, written, ItemStatus.Skipped, EditErrorCode.None, "The output already exists.", false);
            if (job.Output.DeleteOriginal && skipped == 0 && !written.Any(w => string.Equals(w, full, StringComparison.OrdinalIgnoreCase)))
                File.Delete(full);
            return new ItemResult(input, written, ItemStatus.Written, EditErrorCode.None, null, lossless);
        }
        catch (OperationCanceledException)
        {
            return new ItemResult(input, written, ItemStatus.Skipped, EditErrorCode.Cancelled, "Cancelled.", false);
        }
        catch (ImageOperationException ex)
        {
            return Failed(input, written, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed(input, written, EditErrorCode.IoError, ex.Message);
        }
        catch (OutOfMemoryException ex)
        {
            return Failed(input, written, EditErrorCode.TooLarge, ex.Message);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or OverflowException)
        {
            return Failed(input, written, EditErrorCode.EncodeFailed, ex.Message);
        }
    }

    /// <summary>Decode → orientation → resize → rotate/flip → encode (convert) plan.</summary>
    private static List<PlannedOutput> PlanReencode(EditJob job, string full, int index, DateTime date, string extension, ImageFormatKind kind)
    {
        ConvertOptions? convert = job.Convert;
        bool wantAll = convert is null ? ImageFormats.SupportsMultipleFrames(kind) : convert.Frames != FrameMode.FirstOnly;
        DecodedImage decoded = ImageLoader.Guard(full, () => ImageLoader.Decode(full, wantAll));
        FrameMode mode = convert?.Frames ?? (decoded.Frames.Count > 1 ? FrameMode.CombineOne : FrameMode.FirstOnly);

        Dihedral orient = job.Rotate is { AutoRotateByExif: true } ? Dihedral.FromExifOrientation(decoded.Orientation) : Dihedral.Identity;
        Dihedral op = job.Rotate is null ? Dihedral.Identity : Dihedral.From(job.Rotate.Operation);
        bool physicallyRotated = !orient.IsIdentity || !op.IsIdentity;

        // Resize is applied to the upright image, so Width/Height mean what the user sees.
        ResizeOptions? resize = job.Resize;
        ResizeOptions? geometric = resize is { Mode: not ResizeMode.TargetFileSize } ? resize : null;
        List<BitmapSource> frames = [];
        (int W, int H)? size = null;
        foreach (BitmapSource source in decoded.Frames)
        {
            BitmapSource f = ImageTransforms.Apply(source, orient);
            if (geometric is not null)
            {
                size ??= ImageTransforms.ComputeSize(f.PixelWidth, f.PixelHeight, geometric);
                f = ImageTransforms.ScaleTo(f, size.Value.W, size.Value.H);
            }
            f = ImageTransforms.Apply(f, op);
            frames.Add(ImageLoader.Materialize(f));
        }

        int quality = convert?.JpegQuality ?? 85;
        int? targetKb = resize is { Mode: ResizeMode.TargetFileSize } ? resize.TargetKilobytes : null;
        if (targetKb is <= 0)
            throw new ArgumentException("TargetKilobytes must be greater than 0.");
        BitmapMetadata? MetadataFor(int w, int h) => ImageEncoder.CopyMetadata(decoded.Metadata, kind, physicallyRotated, w, h);

        Func<Encoded> EncodeFrames(IReadOnlyList<BitmapSource> group) => () =>
        {
            if (targetKb is { } kb)
            {
                TargetSizeResult r = ImageEncoder.EncodeToTargetSize(group, kind, kb, MetadataFor);
                return new Encoded(r.Bytes, r.Width, r.Height, group.Count);
            }
            int w = group[0].PixelWidth, h = group[0].PixelHeight;
            return new Encoded(ImageEncoder.Encode(group, kind, quality, MetadataFor(w, h)), w, h, group.Count);
        };

        string Name(int? frameNumber) => OutputPlanner.BuildFileName(full, index, date, job.Output, job.Rename, extension, frameNumber);

        return mode switch
        {
            FrameMode.SplitEach when frames.Count > 1 => frames.Select((f, i) => new PlannedOutput(Name(i + 1), EncodeFrames([f]))).ToList(),
            FrameMode.CombineOne => [new PlannedOutput(Name(null), EncodeFrames(frames))],
            _ => [new PlannedOutput(Name(null), EncodeFrames([frames[0]]))],
        };
    }

    /// <summary>
    /// Rotate-only job on a JPEG whose size is a multiple of 16: let WIC transcode the DCT blocks (no recompression).
    /// Returns null when the lossless path does not apply.
    /// </summary>
    private static List<PlannedOutput>? TryPlanLosslessJpeg(EditJob job, string full, int index, DateTime date, string extension)
    {
        if (job.Rotate is null || job.Resize is not null || job.Convert is not null)
            return null;
        if (ImageFormats.FromExtension(Path.GetExtension(full)) != ImageFormatKind.Jpeg || ImageFormats.FromExtension(extension) != ImageFormatKind.Jpeg)
            return null;
        byte[] bytes = File.ReadAllBytes(full);
        Dihedral transform;
        int width, height;
        using (var probe = new MemoryStream(bytes, writable: false))
        {
            BitmapDecoder decoder = ImageLoader.Guard(full, () =>
                BitmapDecoder.Create(probe, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None));
            if (decoder is not JpegBitmapDecoder || decoder.Frames.Count != 1)
                return null;
            BitmapFrame frame = decoder.Frames[0];
            (width, height) = (frame.PixelWidth, frame.PixelHeight);
            ImageLoader.CheckPixels(width, height);
            int orientation = MetadataTools.ReadOrientation(MetadataTools.TryGetMetadata(frame));
            Dihedral orient = job.Rotate.AutoRotateByExif ? Dihedral.FromExifOrientation(orientation) : Dihedral.Identity;
            transform = orient.Then(Dihedral.From(job.Rotate.Operation));
        }
        // Transpose/transverse would need WIC's rotate+flip ordering; partial MCU blocks are not transcoded losslessly.
        if (transform.IsIdentity || (transform.Flip && transform.SwapsAxes) || width % 16 != 0 || height % 16 != 0)
            return null;

        int outW = transform.SwapsAxes ? height : width, outH = transform.SwapsAxes ? width : height;
        string name = OutputPlanner.BuildFileName(full, index, date, job.Output, job.Rename, extension);
        return [new PlannedOutput(name, () => new Encoded(LosslessJpegTransform(bytes, transform), outW, outH, 1))];
    }

    /// <summary>JpegBitmapEncoder on the untouched decoder frame (no pixel cache) so WIC rotates the coefficients.</summary>
    internal static byte[] LosslessJpegTransform(byte[] jpeg, Dihedral transform)
    {
        using var input = new MemoryStream(jpeg, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        int outW = transform.SwapsAxes ? frame.PixelHeight : frame.PixelWidth;
        int outH = transform.SwapsAxes ? frame.PixelWidth : frame.PixelHeight;
        BitmapMetadata? metadata = MetadataTools.CopyFor(MetadataTools.TryGetMetadata(frame), ImageFormatKind.Jpeg, resetOrientation: true, outW, outH);
        System.Collections.ObjectModel.ReadOnlyCollection<System.Windows.Media.ColorContext>? colorContexts;
        try
        {
            colorContexts = frame.ColorContexts;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException)
        {
            colorContexts = null;
        }
        var encoder = new JpegBitmapEncoder();
        if (transform.Flip)
        {
            // Only pure flips reach here: flip-then-0° = horizontal, flip-then-180° = vertical.
            if (transform.Quarter == 0)
                encoder.FlipHorizontal = true;
            else
                encoder.FlipVertical = true;
        }
        else
        {
            encoder.Rotation = transform.Quarter switch
            {
                1 => System.Windows.Media.Imaging.Rotation.Rotate90,
                2 => System.Windows.Media.Imaging.Rotation.Rotate180,
                3 => System.Windows.Media.Imaging.Rotation.Rotate270,
                _ => System.Windows.Media.Imaging.Rotation.Rotate0,
            };
        }
        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, colorContexts));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>Re-decodes the finished temp file; throws unless frame count and size match what was encoded.</summary>
    private static void Verify(string tempPath, Encoded expected)
    {
        try
        {
            using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            if (decoder.Frames.Count != expected.FrameCount || frame.PixelWidth != expected.Width || frame.PixelHeight != expected.Height)
            {
                throw new ImageOperationException(EditErrorCode.EncodeFailed,
                    $"Verification failed: wrote {decoder.Frames.Count} frame(s) {frame.PixelWidth}x{frame.PixelHeight}, expected {expected.FrameCount} {expected.Width}x{expected.Height}.");
            }
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException or InvalidOperationException or ArgumentException)
        {
            throw new ImageOperationException(EditErrorCode.EncodeFailed, "Verification failed: the output does not decode. " + ex.Message, ex);
        }
    }

    private static ItemResult Failed(string input, List<string> written, EditErrorCode code, string detail) =>
        new(input, written, ItemStatus.Failed, code, detail, false);
}
