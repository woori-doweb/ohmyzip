namespace OhMyPic.Core;

/// <summary>
/// Write-to-temp-then-replace. The temp file lives in the target folder (same volume) so the final step is a rename,
/// and it is always removed when anything fails or the write is cancelled.
/// </summary>
public static class AtomicFile
{
    /// <summary>Suffix of in-progress temp files. Anything with this suffix is never a finished output.</summary>
    public const string TempSuffix = ".ohmypic-tmp";

    /// <param name="path">Final path. Its folder must exist.</param>
    /// <param name="write">Writes the full content to the temp stream.</param>
    /// <param name="verify">Optional check of the finished temp file (throw to reject); runs before the target is touched.</param>
    /// <param name="ct">Checked after the temp file is complete, right before the replace.</param>
    public static void Write(string path, Action<Stream> write, Action<string>? verify = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? throw new ArgumentException("Path has no folder.", nameof(path));
        string temp = Path.Combine(folder, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            verify?.Invoke(temp);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void WriteAllBytes(string path, byte[] bytes, Action<string>? verify = null, CancellationToken ct = default) =>
        Write(path, s => s.Write(bytes), verify, ct);

    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
