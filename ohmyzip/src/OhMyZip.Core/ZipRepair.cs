using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>ZIP 복구에서 되살린 항목과 손실 항목을 함께 보고한다.</summary>
public sealed record RepairReport(
    OperationResult Result,
    IReadOnlyList<string> Recovered,
    IReadOnlyList<string> Lost,
    string Method);

/// <summary>
/// 중앙 디렉터리를 믿을 수 없는 ZIP을 지역 파일 머리와 데이터 설명자만으로 복구한다.
/// 복구기는 읽기 전용으로 원본을 열고, 모든 출력은 같은 폴더의 임시 파일에 쓴 뒤
/// 성공한 경우에만 새 경로로 이동한다.
/// </summary>
public static class ZipRepair
{
    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint DataDescriptorSignature = 0x08074B50;
    private const ushort Utf8Flag = 0x0800;
    private const ushort DataDescriptorFlag = 0x0008;
    private const ushort EncryptionFlag = 0x0001;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxRecoveredBytes = 256L * 1024 * 1024;
    private const int MaxScannedEntries = 200_000;
    private const long MaxDescriptorScanBytes = MaxArchiveBytes * 2;
    private const int BufferSize = 81920;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static ZipRepair() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private sealed record LocalEntry(
        int Offset,
        int DataStart,
        ushort Flags,
        ushort Method,
        uint HeaderCrc,
        long HeaderCompressedSize,
        long HeaderSize,
        string RawName,
        string Name,
        bool IsDirectory,
        bool HasDescriptor);

    private sealed record Descriptor(long Offset, long CompressedSize, long Size, uint Crc, int Length);

    private sealed record ParsedEntry(
        LocalEntry Local,
        byte[]? Data,
        int NextOffset,
        uint? ExpectedCrc,
        long? ExpectedSize,
        string? Problem);

    private sealed class RepairFormatException(string message) : Exception(message);

    private sealed class DescriptorScanBudget(long limit)
    {
        private long _remaining = limit;
        private readonly Dictionary<int, int> _headerEnds = [];

        public bool Exhausted { get; private set; }

        public bool TryGetHeaderEnd(int start, out int end) => _headerEnds.TryGetValue(start, out end);

        public void RememberHeaderEnd(int start, int end) => _headerEnds.TryAdd(start, end);

        public bool Visit(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (_remaining <= 0)
            {
                Exhausted = true;
                return false;
            }
            _remaining--;
            return true;
        }
    }

    private const uint FileAttributeReparsePoint = 0x00000400;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    /// <summary>
    /// 손상된 ZIP을 복구해 <paramref name="outputZip"/>에 새 압축 파일로 저장한다.
    /// 기존 출력 파일은 절대 덮어쓰지 않으며, 복구할 수 있는 항목이 하나도 없으면
    /// 임시 출력도 남기지 않는다.
    /// </summary>
    public static RepairReport Repair(
        string damagedZip,
        string outputZip,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken cancel = default)
    {
        var recovered = new List<string>();
        var lost = new List<string>();
        var problems = new List<string>();
        string method = EngineText.Repair_Method;
        string? temporary = null;

        try
        {
            cancel.ThrowIfCancellationRequested();
            string input = Path.GetFullPath(damagedZip);
            string output = Path.GetFullPath(outputZip);
            ValidatePaths(input, output);

            using FileStream inputStream = OpenInput(input);
            byte[] bytes = ReadInput(inputStream, cancel);
            cancel.ThrowIfCancellationRequested();

            var entries = ScanEntries(bytes, progress, cancel, recovered, lost, problems, out bool scanLimitExceeded);
            if (entries.Count == 0)
                return FailedReport(EngineText.Repair_NoLocalHeaders, recovered, lost, method, problems);
            if (scanLimitExceeded)
                return FailedReport(EngineText.Repair_TooManyItems, recovered, lost, method, problems);

            // 이름이 위험하거나 데이터 검증이 끝난 항목만 ZipArchive에 쓴다.
            var valid = entries.Where(e => e.Data is not null && e.Problem is null).ToList();
            if (valid.Count == 0)
            {
                foreach (var entry in entries.Where(e => e.Problem is not null))
                    AddLost(lost, entry.Local.Name);
                return FailedReport(EngineText.Repair_NothingRecovered, recovered, lost, method, problems);
            }

            string? parent = Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(parent))
                parent = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(parent);
            temporary = Path.Combine(
                parent,
                $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.repair.tmp");

            long total = valid.Where(e => !e.Local.IsDirectory).Sum(e => e.Data!.LongLength);
            long processed = 0;
            using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var entry in valid)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = entry.Local.Name;
                    if (entry.Local.IsDirectory)
                    {
                        string directoryName = name.EndsWith('/') ? name : name + "/";
                        archive.CreateEntry(directoryName, CompressionLevel.NoCompression);
                        recovered.Add(name);
                        progress?.Report(new ProgressInfo(processed, total, name));
                        continue;
                    }

                    ZipArchiveEntry outputEntry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using Stream target = outputEntry.Open();
                    byte[] data = entry.Data!;
                    target.Write(data, 0, data.Length);
                    processed += data.LongLength;
                    recovered.Add(name);
                    progress?.Report(new ProgressInfo(processed, total, name));
                }
            }

            cancel.ThrowIfCancellationRequested();
            // Move without overwrite. If another process created the output while we
            // were repairing, the temporary file is removed and the existing file wins.
            File.Move(temporary, output, overwrite: false);
            temporary = null;

            foreach (var entry in entries.Where(e => e.Problem is not null))
            {
                AddLost(lost, entry.Local.Name);
                if (!string.IsNullOrEmpty(entry.Problem))
                    problems.Add($"{entry.Local.Name}: {entry.Problem}");
            }

            OperationResult result = lost.Count == 0
                ? OperationResult.Ok(string.Format(EngineText.Repair_Recovered, recovered.Count))
                : new OperationResult(
                    OperationStatus.Succeeded,
                    string.Format(EngineText.Repair_RecoveredWithLost, recovered.Count, lost.Count),
                    problems);
            return new RepairReport(result, recovered, lost, method);
        }
        catch (OperationCanceledException)
        {
            DeleteTemporary(temporary);
            return new RepairReport(OperationResult.Cancelled(EngineText.Repair_Cancelled), recovered, lost, method);
        }
        catch (RepairFormatException ex)
        {
            DeleteTemporary(temporary);
            return FailedReport(ex.Message, recovered, lost, method, problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DeleteTemporary(temporary);
            return FailedReport(EngineText.Repair_Error, recovered, lost, method, [ex.Message]);
        }
        catch (Exception ex)
        {
            DeleteTemporary(temporary);
            return FailedReport(EngineText.Repair_FormatError, recovered, lost, method, [ex.Message]);
        }
    }

    private static RepairReport FailedReport(
        string message,
        IReadOnlyList<string> recovered,
        IReadOnlyList<string> lost,
        string method,
        IEnumerable<string>? problems = null) =>
        new(
            new OperationResult(OperationStatus.Failed, message, problems?.ToArray() ?? []),
            recovered,
            lost,
            method);

    private static FileStream OpenInput(string input)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(EngineText.Repair_WindowsOnly);
        try
        {
            // Open the final component without following a reparse point and
            // deny writers/deletion while reading the validated handle.
            SafeFileHandle handle = CreateFile(input, 0x80000000, 1, IntPtr.Zero,
                3, 0x08000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(EngineText.Repair_CannotOpen,
                    new System.ComponentModel.Win32Exception(error));
            }
            FileStream stream;
            try
            {
                stream = new FileStream(handle, FileAccess.Read, BufferSize, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            try
            {
                ByHandleFileInformation info = GetHandleInformation(stream);
                if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new RepairFormatException(EngineText.Repair_ReparsePointInput);
                long length = HandleLength(info);
                if (length != stream.Length)
                    throw new RepairFormatException(EngineText.Repair_InputSizeUnknown);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException)
        {
            throw new RepairFormatException(EngineText.Repair_InputMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new RepairFormatException(EngineText.Repair_InputMissing);
        }
        catch (UnauthorizedAccessException)
        {
            throw new RepairFormatException(EngineText.Repair_CannotOpen);
        }
        catch (IOException ex)
        {
            throw new RepairFormatException(string.Format(EngineText.Repair_CannotOpenDetail, ex.Message));
        }
    }

    private static ByHandleFileInformation GetHandleInformation(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation info))
            throw new RepairFormatException(EngineText.Repair_InputAttributesUnknown);
        return info;
    }

    private static long HandleLength(ByHandleFileInformation info)
    {
        ulong length = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        return length > long.MaxValue ? long.MaxValue : (long)length;
    }

    private static byte[] ReadInput(FileStream stream, CancellationToken cancel)
    {
        ByHandleFileInformation initial = GetHandleInformation(stream);
        long length = HandleLength(initial);
        if (length <= 0)
            throw new RepairFormatException(EngineText.Repair_InputEmpty);
        if (length > MaxArchiveBytes || length > int.MaxValue)
            throw new RepairFormatException(EngineText.Repair_InputTooLarge);

        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);
        stream.Position = 0;
        int read = 0;
        while (read < bytes.Length)
        {
            cancel.ThrowIfCancellationRequested();
            int n = stream.Read(bytes, read, bytes.Length - read);
            if (n <= 0)
                throw new RepairFormatException(EngineText.Repair_InputShrank);
            read += n;
        }
        cancel.ThrowIfCancellationRequested();
        if (stream.ReadByte() >= 0)
            throw new RepairFormatException(EngineText.Repair_InputGrew);
        ByHandleFileInformation final = GetHandleInformation(stream);
        if (HandleLength(final) != length
            || (final.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new RepairFormatException(EngineText.Repair_InputChanged);
        return bytes;
    }

    private static void ValidatePaths(string input, string output)
    {
        if (!File.Exists(input))
            throw new RepairFormatException(EngineText.Repair_InputMissing);
        if (Directory.Exists(input))
            throw new RepairFormatException(EngineText.Repair_InputIsFolder);
        if (File.Exists(output) || Directory.Exists(output))
            throw new RepairFormatException(EngineText.Repair_OutputExists);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new RepairFormatException(EngineText.Repair_SamePaths);
        try
        {
            if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
                throw new RepairFormatException(EngineText.Repair_ReparsePointInput);
        }
        catch (FileNotFoundException)
        {
            throw new RepairFormatException(EngineText.Repair_InputMissing);
        }
    }

    private static List<ParsedEntry> ScanEntries(
        byte[] bytes,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancel,
        List<string> recovered,
        List<string> lost,
        List<string> problems,
        out bool scanLimitExceeded)
    {
        var entries = new List<ParsedEntry>();
        long recoveredBytes = 0;
        var scanBudget = new DescriptorScanBudget(MaxDescriptorScanBytes);
        scanLimitExceeded = false;
        int cursor = 0;
        while (TryFindSignature(bytes, cursor, LocalHeaderSignature, cancel, scanBudget, out int offset))
        {
            cancel.ThrowIfCancellationRequested();
            if (entries.Count >= MaxScannedEntries)
            {
                scanLimitExceeded = true;
                problems.Add(string.Format(EngineText.Repair_LocalItemLimit, MaxScannedEntries));
                break;
            }
            if (!TryReadLocal(bytes, offset, out LocalEntry? local, out string? parseError))
            {
                // A random signature in file data is not an entry. Continue at the
                // following byte, while preserving malformed-signature diagnostics.
                if (parseError is not null && offset == cursor)
                    problems.Add(parseError);
                cursor = offset + 4;
                continue;
            }

            ParsedEntry entry = ParseEntry(bytes, local!, cancel, scanBudget);
            if (entry.Data is not null
                && entry.Problem is null
                && (entry.Data.LongLength > MaxRecoveredBytes
                    || recoveredBytes > MaxRecoveredBytes - entry.Data.LongLength))
            {
                entry = entry with
                {
                    Data = null,
                    Problem = EngineText.Repair_TotalLimit
                };
            }
            entries.Add(entry);
            if (entry.Data is not null && entry.Problem is null)
            {
                recoveredBytes += entry.Data.LongLength;
                recovered.Add(local!.Name);
            }
            else
            {
                AddLost(lost, local!.Name);
                if (entry.Problem is not null)
                    problems.Add($"{local.Name}: {entry.Problem}");
            }
            cursor = Math.Max(entry.NextOffset, offset + 4);
            progress?.Report(new ProgressInfo(Math.Min(cursor, bytes.Length), bytes.Length, local.Name));
            if (cursor >= bytes.Length)
                break;
        }
        scanLimitExceeded = scanLimitExceeded || scanBudget.Exhausted;
        // Recover() writes the list itself so that entries rejected after a
        // successful parse are not duplicated in the result.
        recovered.Clear();
        lost.Clear();
        return entries;
    }

    private static bool TryReadLocal(byte[] bytes, int offset, out LocalEntry? local, out string? error)
    {
        local = null;
        error = null;
        if (!HasBytes(bytes, offset, 30)
            || ReadUInt32(bytes, offset) != LocalHeaderSignature)
        {
            error = EngineText.Repair_LocalHeaderDamaged;
            return false;
        }

        ushort flags = ReadUInt16(bytes, offset + 6);
        ushort method = ReadUInt16(bytes, offset + 8);
        uint headerCrc = ReadUInt32(bytes, offset + 14);
        uint compressed32 = ReadUInt32(bytes, offset + 18);
        uint size32 = ReadUInt32(bytes, offset + 22);
        int nameLength = ReadUInt16(bytes, offset + 26);
        int extraLength = ReadUInt16(bytes, offset + 28);
        long headerLength = 30L + nameLength + extraLength;
        if (headerLength > int.MaxValue || !HasBytes(bytes, offset, (int)headerLength))
        {
            error = EngineText.Repair_LocalHeaderOutside;
            return false;
        }

        int dataStart = offset + (int)headerLength;
        ReadOnlySpan<byte> nameBytes = bytes.AsSpan(offset + 30, nameLength);
        ReadOnlySpan<byte> extra = bytes.AsSpan(offset + 30 + nameLength, extraLength);
        string rawName;
        try
        {
            rawName = DecodeName(nameBytes, flags);
        }
        catch (DecoderFallbackException)
        {
            error = EngineText.Repair_NameEncodingInvalid;
            return false;
        }

        bool zip64Compressed = compressed32 == uint.MaxValue;
        bool zip64Size = size32 == uint.MaxValue;
        long compressed = zip64Compressed ? -1 : compressed32;
        long size = zip64Size ? -1 : size32;
        // Writers commonly leave both local size fields as zero when bit 3
        // requests a data descriptor. A zero-sized stored file is still
        // handled correctly once its descriptor is found.
        if ((flags & DataDescriptorFlag) != 0)
        {
            if (compressed == 0)
                compressed = -1;
            if (size == 0)
                size = -1;
        }
        if ((zip64Compressed || zip64Size) && !TryReadZip64Sizes(extra, ref compressed, ref size))
        {
            error = EngineText.Repair_Zip64SizesMissing;
            return false;
        }
        if (compressed < -1 || size < -1)
        {
            error = EngineText.Common_ItemSizeInvalid;
            return false;
        }

        bool directory = SafePaths.EndsWithSeparator(rawName);
        string safeName = "";
        if (!directory)
        {
            if (!SafePaths.TryGetSafeRelative(rawName, out safeName, out string safeError))
            {
                // Keep the decoded name in the report but never pass it to
                // ZipArchive. The entry will be marked lost by ParseEntry.
                local = new LocalEntry(
                    offset,
                    dataStart,
                    flags,
                    method,
                    headerCrc,
                    compressed,
                    size,
                    rawName,
                    rawName,
                    false,
                    (flags & DataDescriptorFlag) != 0);
                error = safeError;
                return true;
            }
        }
        else
        {
            if (!SafePaths.TryGetSafeRelative(rawName, out string safeDirectory, out string directoryError))
            {
                local = new LocalEntry(
                    offset,
                    dataStart,
                    flags,
                    method,
                    headerCrc,
                    compressed,
                    size,
                    rawName,
                    rawName,
                    true,
                    (flags & DataDescriptorFlag) != 0);
                error = directoryError;
                return true;
            }
            safeName = safeDirectory;
        }

        local = new LocalEntry(
            offset,
            dataStart,
            flags,
            method,
            headerCrc,
            compressed,
            size,
            rawName,
            safeName,
            directory,
            (flags & DataDescriptorFlag) != 0);
        return true;
    }

    private static ParsedEntry ParseEntry(
        byte[] bytes,
        LocalEntry local,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        // A zero-byte directory's next local header begins exactly at
        // DataStart. Advancing by DataStart+4 would skip that header (the
        // first file after an explicit directory), so the safety fallback is
        // based on the header offset itself.
        int fallbackNext = Math.Min(bytes.Length, local.Offset + 4);
        if (!SafePaths.TryGetSafeRelative(local.RawName, out _, out string pathError))
            return new ParsedEntry(local, null, FindNextHeader(bytes, local.DataStart, cancel, scanBudget), null, null, pathError);
        if ((local.Flags & EncryptionFlag) != 0)
            return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, EngineText.Repair_EncryptedSkipped);
        if (local.Method is not (0 or 8))
            return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, string.Format(EngineText.Common_MethodUnsupported, local.Method));
        if (local.HeaderSize > MaxEntryBytes)
            return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, EngineText.Repair_SizeLimit);
        if (local.HeaderCompressedSize > MaxArchiveBytes)
            return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, EngineText.Repair_CompressedSizeInvalid);

        try
        {
            cancel.ThrowIfCancellationRequested();
            if (local.IsDirectory)
            {
                if (!local.HasDescriptor
                    && (local.HeaderCompressedSize != 0 || local.HeaderSize != 0 || local.HeaderCrc != 0))
                    return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, EngineText.Repair_FolderSizeNotZero);
                int next = local.DataStart;
                if (local.HasDescriptor)
                {
                    next = FindDescriptorAfterEmpty(
                        bytes,
                        local.DataStart,
                        local.HeaderCrc,
                        local.HeaderCompressedSize,
                        local.HeaderSize,
                        out Descriptor? descriptor);
                    if (descriptor is null || descriptor.CompressedSize != 0 || descriptor.Size != 0 || descriptor.Crc != 0)
                        return new ParsedEntry(local, null, Math.Max(next, fallbackNext), null, null, EngineText.Repair_FolderDescriptorInvalid);
                }
                return new ParsedEntry(local, [], Math.Max(next, fallbackNext), 0, 0, null);
            }

            if (local.HeaderCompressedSize >= 0)
                return ParseKnownSized(bytes, local, cancel, scanBudget);
            return ParseStreamed(bytes, local, cancel, scanBudget);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or RepairFormatException or EndOfStreamException)
        {
            int next = SkipKnownData(bytes, local, cancel, scanBudget);
            return new ParsedEntry(local, null, Math.Max(next, fallbackNext), null, null, ex.Message);
        }
    }

    private static ParsedEntry ParseKnownSized(
        byte[] bytes,
        LocalEntry local,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        long compressed = local.HeaderCompressedSize;
        long size = local.HeaderSize;
        if (compressed < 0 || compressed > int.MaxValue || local.DataStart > bytes.Length - compressed)
            return new ParsedEntry(local, null, FindNextHeader(bytes, local.DataStart, cancel, scanBudget), null, null, EngineText.Repair_DataOutside);
        if (size > MaxEntryBytes)
            return new ParsedEntry(local, null, SkipKnownData(bytes, local, cancel, scanBudget), null, null, EngineText.Repair_SizeLimit);

        int dataEnd = checked(local.DataStart + (int)compressed);
        ReadOnlySpan<byte> compressedData = bytes.AsSpan(local.DataStart, (int)compressed);
        byte[] data;
        if (local.Method == 0)
        {
            data = compressedData.ToArray();
        }
        else
        {
            data = Inflate(compressedData, size, cancel, out int consumed);
            if (consumed != compressedData.Length)
                return new ParsedEntry(local, null, dataEnd, null, size, EngineText.Repair_LengthMismatchHeader);
        }

        Descriptor? descriptor = null;
        if (local.HasDescriptor)
            descriptor = TryReadDescriptor(bytes, dataEnd, local.DataStart, data.LongLength, preferSignature: true);

        uint? expectedCrc = descriptor?.Crc ?? (local.HasDescriptor && local.HeaderCrc == 0 ? null : local.HeaderCrc);
        long? expectedSize = descriptor?.Size ?? (size >= 0 ? size : null);
        int next = descriptor is null ? dataEnd : DescriptorEnd(descriptor);
        if (descriptor is null && local.HasDescriptor)
        {
            // A missing descriptor is a malformed streamed entry. Do not
            // silently treat its trailing bytes as another valid archive.
            return new ParsedEntry(local, null, dataEnd, null, null, EngineText.Repair_DescriptorNotFound);
        }
        if (expectedSize is { } expected && expected != data.LongLength)
            return new ParsedEntry(local, null, Math.Max(next, dataEnd), expectedCrc, expected, EngineText.Repair_SizeMismatch);
        if (expectedCrc is { } crc && ZipCentralDirectory.Crc(data) != crc)
            return new ParsedEntry(local, null, Math.Max(next, dataEnd), crc, expectedSize, EngineText.Repair_CrcMismatch);
        return new ParsedEntry(local, data, Math.Max(next, dataEnd), expectedCrc, expectedSize, null);
    }

    private static ParsedEntry ParseStreamed(
        byte[] bytes,
        LocalEntry local,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        if (local.Method == 0)
        {
            foreach (Descriptor descriptor in FindDescriptors(bytes, local.DataStart, local.HeaderSize, cancel, scanBudget))
            {
                cancel.ThrowIfCancellationRequested();
                if (descriptor.CompressedSize > int.MaxValue
                    || descriptor.Offset < local.DataStart
                    || descriptor.Offset > bytes.Length
                    || descriptor.Offset - local.DataStart > bytes.Length)
                    continue;
                int compressed = checked((int)(descriptor.Offset - local.DataStart));
                if (descriptor.CompressedSize != compressed
                    || descriptor.Size != compressed
                    || descriptor.Size > MaxEntryBytes)
                    continue;
                byte[] data = bytes.AsSpan(local.DataStart, compressed).ToArray();
                if (data.LongLength == descriptor.Size
                    && ZipCentralDirectory.Crc(data) == descriptor.Crc)
                    return new ParsedEntry(local, data, DescriptorEnd(descriptor), descriptor.Crc, descriptor.Size, null);
            }
            return new ParsedEntry(local, null, FindNextHeader(bytes, local.DataStart, cancel, scanBudget), null, null, EngineText.Repair_StoredUnverified);
        }

        // Locate a descriptor before inflating. This bounds the compressed
        // input and prevents DeflateStream from consuming a following header.
        var candidates = FindDescriptors(bytes, local.DataStart, local.HeaderSize, cancel, scanBudget);
        foreach (Descriptor descriptor in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (descriptor.CompressedSize > int.MaxValue
                || descriptor.CompressedSize < 0
                || descriptor.Offset < local.DataStart
                || descriptor.Offset - local.DataStart != descriptor.CompressedSize
                || descriptor.Size < 0
                || descriptor.Size > MaxEntryBytes)
                continue;
            try
            {
                byte[] compressed = bytes.AsSpan(local.DataStart, checked((int)descriptor.CompressedSize)).ToArray();
                byte[] data = Inflate(compressed, descriptor.Size, cancel, out int consumed);
                if (consumed != compressed.Length)
                    return new ParsedEntry(local, null, DescriptorEnd(descriptor), descriptor.Crc, descriptor.Size, EngineText.Repair_LengthMismatchDescriptor);
                if (data.LongLength != descriptor.Size || ZipCentralDirectory.Crc(data) != descriptor.Crc)
                    return new ParsedEntry(
                        local,
                        null,
                        DescriptorEnd(descriptor),
                        descriptor.Crc,
                        descriptor.Size,
                        data.LongLength == descriptor.Size ? EngineText.Repair_CrcMismatch : EngineText.Repair_SizeMismatch);
                if (local.HeaderSize >= 0 && local.HeaderSize != descriptor.Size)
                    return new ParsedEntry(local, null, DescriptorEnd(descriptor), descriptor.Crc, descriptor.Size, EngineText.Repair_SizeMismatch);
                return new ParsedEntry(local, data, DescriptorEnd(descriptor), descriptor.Crc, descriptor.Size, null);
            }
            catch (InvalidDataException)
            {
                // A descriptor-shaped byte sequence can occur in deflate data.
                // Try the next size-consistent candidate before giving up.
            }
        }

        // Without a descriptor (or a non-zero local CRC) there is no
        // integrity value to verify. Never report such a payload as recovered.
        return new ParsedEntry(
            local,
            null,
            FindNextHeader(bytes, local.DataStart, cancel, scanBudget),
            null,
            null,
            EngineText.Repair_StreamUnverified);
    }

    private static byte[] Inflate(
        ReadOnlySpan<byte> compressed,
        long expectedSize,
        CancellationToken cancel,
        out int consumed)
    {
        using var input = new ByteWindowStream(compressed.ToArray(), singleByteReads: true);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        byte[] buffer = new byte[BufferSize];
        int n;
        while ((n = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (output.Length > MaxEntryBytes - n)
                throw new InvalidDataException(EngineText.Repair_SizeLimit);
            output.Write(buffer, 0, n);
            if (expectedSize >= 0 && output.Length > expectedSize)
                throw new InvalidDataException(EngineText.Repair_LargerThanHeader);
        }
        consumed = checked((int)input.Position);
        if (consumed <= 0)
            throw new InvalidDataException(EngineText.Repair_DeflateEmpty);
        return output.ToArray();
    }

    private static string DecodeName(ReadOnlySpan<byte> nameBytes, ushort flags)
    {
        if ((flags & Utf8Flag) != 0)
            return StrictUtf8.GetString(nameBytes);
        try
        {
            return StrictUtf8.GetString(nameBytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949).GetString(nameBytes);
        }
    }

    private static bool TryReadZip64Sizes(ReadOnlySpan<byte> extra, ref long compressed, ref long size)
    {
        byte[]? field = ZipCentralDirectory.FindExtra(extra, ZipCentralDirectory.Zip64ExtraId);
        if (field is null)
            return false;
        int p = 0;
        try
        {
            if (size < 0)
            {
                if (p + 8 > field.Length)
                    return false;
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(field.AsSpan(p));
                if (value > long.MaxValue)
                    return false;
                size = (long)value;
                p += 8;
            }
            if (compressed < 0)
            {
                if (p + 8 > field.Length)
                    return false;
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(field.AsSpan(p));
                if (value > long.MaxValue)
                    return false;
                compressed = (long)value;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Descriptor? TryReadDescriptor(
        byte[] bytes,
        int offset,
        int dataStart,
        long uncompressedSize,
        bool preferSignature)
    {
        if (offset < 0 || offset > bytes.Length)
            return null;
        if (preferSignature && HasBytes(bytes, offset, 16) && ReadUInt32(bytes, offset) == DataDescriptorSignature)
        {
            var d = ReadDescriptorFields(bytes, offset, signature: true, zip64: false);
            if (d is not null && d.CompressedSize == offset - dataStart && d.Size == uncompressedSize)
                return d;
            if (HasBytes(bytes, offset, 24))
            {
                d = ReadDescriptorFields(bytes, offset, signature: true, zip64: true);
                if (d is not null && d.CompressedSize == offset - dataStart && d.Size == uncompressedSize)
                    return d;
            }
        }
        if (HasBytes(bytes, offset, 12))
        {
            var d = ReadDescriptorFields(bytes, offset, signature: false, zip64: false);
            if (d is not null && d.CompressedSize == offset - dataStart && d.Size == uncompressedSize)
                return d;
            if (HasBytes(bytes, offset, 20))
            {
                d = ReadDescriptorFields(bytes, offset, signature: false, zip64: true);
                if (d is not null && d.CompressedSize == offset - dataStart && d.Size == uncompressedSize)
                    return d;
            }
        }
        return null;
    }

    private static IEnumerable<Descriptor> FindDescriptors(
        byte[] bytes,
        int dataStart,
        long expectedSize,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        int scanEnd = FindNextHeader(bytes, dataStart, cancel, scanBudget);
        int yielded = 0;
        var seen = new HashSet<long>();
        int lastCandidate = Math.Min(scanEnd, bytes.Length - 4);
        for (int p = dataStart; p <= lastCandidate && yielded < 128; p++)
        {
            if (!scanBudget.Visit(cancel))
                yield break;

            // Signed descriptors can occur anywhere before the next ZIP
            // structure. A candidate is only useful when its compressed size
            // agrees with its own offset from the local data start.
            if (p + 16 <= bytes.Length && ReadUInt32(bytes, p) == DataDescriptorSignature)
            {
                Descriptor? descriptor = p + 24 <= bytes.Length
                    ? ReadDescriptorFields(bytes, p, signature: true, zip64: true)
                    : null;
                if (!IsDescriptorCandidate(descriptor, dataStart, expectedSize))
                {
                    descriptor = ReadDescriptorFields(bytes, p, signature: true, zip64: false);
                }
                if (IsDescriptorCandidate(descriptor, dataStart, expectedSize)
                    && seen.Add(descriptor!.Offset))
                {
                    yield return descriptor;
                    yielded++;
                }
            }

            // An unsigned descriptor is identified by the next local header,
            // central directory, or EOCD. Check both classic and ZIP64
            // layouts at that exact boundary.
            if (p + 4 <= bytes.Length
                && ReadUInt32(bytes, p) is (LocalHeaderSignature or CentralHeaderSignature or EndOfCentralDirectorySignature))
            {
                int candidateOffset = p - 20;
                Descriptor? descriptor = candidateOffset >= dataStart
                    ? ReadDescriptorFields(bytes, candidateOffset, signature: false, zip64: true)
                    : null;
                if (!IsDescriptorCandidate(descriptor, dataStart, expectedSize))
                {
                    candidateOffset = p - 12;
                    descriptor = candidateOffset >= dataStart
                        ? ReadDescriptorFields(bytes, candidateOffset, signature: false, zip64: false)
                        : null;
                }
                if (IsDescriptorCandidate(descriptor, dataStart, expectedSize)
                    && seen.Add(descriptor!.Offset))
                {
                    yield return descriptor;
                    yielded++;
                }
            }
        }

        // A final unsigned descriptor may be followed by EOF rather than a
        // central directory. Keep this as one bounded check, not another scan.
        if (yielded < 128 && scanEnd == bytes.Length)
        {
            foreach (int candidateOffset in new[] { bytes.Length - 20, bytes.Length - 12 })
            {
                cancel.ThrowIfCancellationRequested();
                if (candidateOffset < dataStart)
                    continue;
                Descriptor? descriptor = ReadDescriptorFields(
                    bytes,
                    candidateOffset,
                    signature: false,
                    zip64: candidateOffset == bytes.Length - 20);
                if (IsDescriptorCandidate(descriptor, dataStart, expectedSize)
                    && seen.Add(descriptor!.Offset))
                {
                    yield return descriptor;
                    break;
                }
            }
        }
    }

    private static bool IsDescriptorCandidate(
        Descriptor? descriptor,
        int dataStart,
        long expectedSize)
    {
        if (descriptor is null
            || descriptor.Offset < dataStart
            || descriptor.CompressedSize != descriptor.Offset - dataStart
            || descriptor.Size < 0
            || descriptor.Size > MaxEntryBytes)
            return false;
        return expectedSize < 0 || descriptor.Size == expectedSize;
    }

    private static Descriptor? ReadDescriptorFields(byte[] bytes, int offset, bool signature, bool zip64)
    {
        int length = (signature ? 4 : 0) + 4 + (zip64 ? 16 : 8);
        if (!HasBytes(bytes, offset, length))
            return null;
        int p = offset + (signature ? 4 : 0);
        uint crc = ReadUInt32(bytes, p);
        p += 4;
        ulong compressed = zip64
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p))
            : ReadUInt32(bytes, p);
        p += zip64 ? 8 : 4;
        ulong size = zip64
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p))
            : ReadUInt32(bytes, p);
        if (compressed > long.MaxValue || size > long.MaxValue)
            return null;
        return new Descriptor(offset, (long)compressed, (long)size, crc, length);
    }

    private static int DescriptorEnd(Descriptor descriptor)
    {
        if (descriptor.Offset < 0
            || descriptor.Length < 0
            || descriptor.Offset > int.MaxValue
            || descriptor.Offset > int.MaxValue - descriptor.Length)
            throw new InvalidDataException(EngineText.Repair_DescriptorLocationInvalid);
        return checked((int)descriptor.Offset + descriptor.Length);
    }

    private static int FindDescriptorAfterEmpty(
        byte[] bytes,
        int dataStart,
        uint headerCrc,
        long compressed,
        long size,
        out Descriptor? descriptor)
    {
        descriptor = TryReadDescriptor(bytes, dataStart, dataStart, 0, preferSignature: true);
        return descriptor is null ? dataStart : DescriptorEnd(descriptor);
    }

    private static int SkipKnownData(
        byte[] bytes,
        LocalEntry local,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        if (local.HeaderCompressedSize >= 0
            && local.HeaderCompressedSize <= int.MaxValue
            && local.DataStart <= bytes.Length - local.HeaderCompressedSize)
        {
            int end = local.DataStart + (int)local.HeaderCompressedSize;
            if (local.HasDescriptor)
            {
                Descriptor? descriptor = TryReadDescriptor(bytes, end, local.DataStart, local.HeaderSize, preferSignature: true);
                if (descriptor is not null)
                    return DescriptorEnd(descriptor);
            }
            return end;
        }
        return FindNextHeader(bytes, local.DataStart, cancel, scanBudget);
    }

    private static int FindNextHeader(
        byte[] bytes,
        int start,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        if (start < 0 || start >= bytes.Length)
            return bytes.Length;
        if (scanBudget.TryGetHeaderEnd(start, out int cached))
            return cached;
        for (int p = start; p + 4 <= bytes.Length; p++)
        {
            if (!scanBudget.Visit(cancel))
            {
                scanBudget.RememberHeaderEnd(start, bytes.Length);
                return bytes.Length;
            }
            uint signature = ReadUInt32(bytes, p);
            if (signature is LocalHeaderSignature or CentralHeaderSignature or EndOfCentralDirectorySignature)
            {
                scanBudget.RememberHeaderEnd(start, p);
                return p;
            }
        }
        scanBudget.RememberHeaderEnd(start, bytes.Length);
        return bytes.Length;
    }

    private static bool TryFindSignature(
        byte[] bytes,
        int start,
        uint signature,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget,
        out int offset)
    {
        offset = FindSignature(bytes, start, signature, cancel, scanBudget);
        return offset >= 0;
    }

    private static int FindSignature(
        byte[] bytes,
        int start,
        uint signature,
        CancellationToken cancel,
        DescriptorScanBudget scanBudget)
    {
        if (start < 0)
            start = 0;
        for (int p = start; p + 4 <= bytes.Length; p++)
        {
            if (!scanBudget.Visit(cancel))
                return -1;
            if (ReadUInt32(bytes, p) == signature)
                return p;
        }
        return -1;
    }

    private static bool HasBytes(byte[] bytes, int offset, long count) =>
        offset >= 0 && count >= 0 && offset <= bytes.Length && count <= bytes.Length - offset;

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static void AddLost(List<string> lost, string name)
    {
        if (!lost.Contains(name, StringComparer.Ordinal))
            lost.Add(name);
    }

    private static void DeleteTemporary(string? path)
    {
        if (path is null)
            return;
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

    /// <summary>Deflate 스트림이 다음 ZIP 구조물을 미리 읽지 않도록 1바이트씩 공급한다.</summary>
    private sealed class ByteWindowStream(byte[] data, bool singleByteReads) : Stream
    {
        private readonly byte[] _data = data;
        private readonly bool _singleByteReads = singleByteReads;
        private int _position;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int available = _data.Length - _position;
            if (available <= 0 || buffer.Length == 0)
                return 0;
            int count = Math.Min(available, buffer.Length);
            if (_singleByteReads)
                count = 1;
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int ReadByte()
        {
            if (_position >= _data.Length)
                return -1;
            return _data[_position++];
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
