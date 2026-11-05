using Microsoft.VisualBasic.FileIO;

namespace OhMyPic.Core;

/// <summary>Copy / move / rename / delete for image files.</summary>
public static class FileOps
{
    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="targetFolder"/> (created if missing). On a name clash
    /// <paramref name="onConflict"/> decides (null = skip). Copying into the source's own folder always renames.
    /// Returns the written path, or null when skipped.
    /// </summary>
    public static string? CopyTo(string source, string targetFolder, Func<ConflictInfo, ConflictDecision>? onConflict = null)
    {
        string src = Path.GetFullPath(source);
        if (!File.Exists(src))
            throw new FileNotFoundException("The file does not exist.", src);
        string? target = ResolveTarget(src, targetFolder, onConflict, sameFolderMeansRename: true);
        if (target is null)
            return null;
        AtomicFile.Write(target, output =>
        {
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(output);
        });
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(src));
        return target;
    }

    /// <summary>Moves like <see cref="CopyTo"/>. Moving into the same folder is a no-op that returns the source path.</summary>
    public static string? MoveTo(string source, string targetFolder, Func<ConflictInfo, ConflictDecision>? onConflict = null)
    {
        string src = Path.GetFullPath(source);
        if (!File.Exists(src))
            throw new FileNotFoundException("The file does not exist.", src);
        if (string.Equals(Path.GetDirectoryName(src), Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return src;
        string? target = ResolveTarget(src, targetFolder, onConflict, sameFolderMeansRename: false);
        if (target is null)
            return null;
        File.Move(src, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Renames within the same folder. Without an extension in <paramref name="newName"/> the original one is kept.
    /// Returns the new path.
    /// </summary>
    /// <exception cref="ArgumentException">Empty or invalid name.</exception>
    /// <exception cref="IOException">Another file already has that name.</exception>
    public static string Rename(string path, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        string src = Path.GetFullPath(path);
        if (!File.Exists(src))
            throw new FileNotFoundException("The file does not exist.", src);
        string name = newName.Trim();
        RenameRule.Validate(name);
        if (Path.GetExtension(name).Length == 0)
            name += Path.GetExtension(src);
        string target = Path.Combine(Path.GetDirectoryName(src)!, name);
        if (string.Equals(target, src, StringComparison.Ordinal))
            return src;
        bool caseOnly = string.Equals(target, src, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException($"'{name}' already exists.");
        File.Move(src, target);
        return target;
    }

    public static void DeletePermanently(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full))
            File.Delete(full);
    }

    public static void DeletePermanently(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            DeletePermanently(path);
    }

    /// <summary>Sends files to the Recycle Bin (shell shows only error dialogs). Not exercised by automated tests.</summary>
    public static void DeleteToRecycleBin(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full))
                FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    private static string? ResolveTarget(string src, string targetFolder, Func<ConflictInfo, ConflictDecision>? onConflict, bool sameFolderMeansRename)
    {
        string folder = Path.GetFullPath(targetFolder);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(src));
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;
        if (sameFolderMeansRename && string.Equals(target, src, StringComparison.OrdinalIgnoreCase))
            return OutputPlanner.MakeUnique(target);
        ConflictDecision decision = onConflict?.Invoke(new ConflictInfo(src, target)) ?? ConflictDecision.Skip;
        return decision switch
        {
            ConflictDecision.Overwrite when !Directory.Exists(target) => target,
            ConflictDecision.Rename => OutputPlanner.MakeUnique(target),
            _ => null,
        };
    }
}
