using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.LZMA;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>압축할 원본 하나(파일 또는 폴더). EntryName 은 '/' 구분, 끝 '/' 없음. FilePath 는 디스크의 원본 경로.</summary>
internal sealed record SourceEntry(string EntryName, string? FilePath, bool IsDirectory);

/// <summary>원본 파일·폴더를 항목 목록으로 펼치는 공용 규칙(7Z·TAR). 폴더는 이름째, 빈 폴더 포함, 링크 폴더는 따라가지 않는다.</summary>
internal static class SourceTree
{
    public static List<SourceEntry> Expand(IReadOnlyList<string> sources, string prefix, ISet<string> exclude, List<string> problems)
    {
        var items = new List<SourceEntry>();
        foreach (string source in sources)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add(string.Format(FormatText.Source_PathInvalid, source));
                continue;
            }
            string name = Path.GetFileName(full);
            if (File.Exists(full))
                items.Add(new SourceEntry(prefix + name, full, false));
            else if (Directory.Exists(full) && name.Length > 0)
                AddFolder(new DirectoryInfo(full), prefix + name, items, exclude);
            else
                problems.Add(string.Format(FormatText.Source_Missing, source));
        }
        return items;
    }

    private static void AddFolder(DirectoryInfo dir, string entryName, List<SourceEntry> items, ISet<string> exclude)
    {
        items.Add(new SourceEntry(entryName, dir.FullName, true));
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            return;
        foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (exclude.Contains(child.FullName))
                continue;
            if (child is DirectoryInfo sub)
                AddFolder(sub, entryName + "/" + sub.Name, items, exclude);
            else
                items.Add(new SourceEntry(entryName + "/" + child.Name, child.FullName, false));
        }
    }

    public static string? DuplicateName(IEnumerable<SourceEntry> items)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (!names.Add(i.EntryName))
                return i.EntryName;
        }
        return null;
    }

    public static long TotalBytes(IEnumerable<SourceEntry> items) =>
        items.Where(i => !i.IsDirectory && i.FilePath is not null).Sum(i => new FileInfo(i.FilePath!).Length);

    /// <summary>원본의 수정 시각(UTC). 읽지 못하면 null.</summary>
    public static DateTime? ModifiedUtc(SourceEntry e)
    {
        if (e.FilePath is null)
            return null;
        try
        {
            return e.IsDirectory ? Directory.GetLastWriteTimeUtc(e.FilePath) : File.GetLastWriteTimeUtc(e.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Func<Stream> OpenFile(string path) =>
        () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
}

/// <summary>7Z 에 쓸 항목. Name 은 검증 없이 그대로 기록된다(엔진이 미리 검증; 시험은 위험한 이름을 일부러 넣는다).</summary>
internal sealed record SevenZipItem(string Name, bool IsDirectory, DateTime? ModifiedUtc, Func<Stream>? Open);

/// <summary>Level 0 = Copy 코더, 1~9 = LZMA. Password 가 있으면 AES-256, EncryptNames 면 헤더도 LZMA+AES 로 감싼다.</summary>
internal sealed record SevenZipWriteOptions(int Level, string? Password = null, bool EncryptNames = false);

/// <summary>
/// 7Z 쓰기(BCL + SharpCompress LZMA 인코더). 구조: 시작 헤더(32바이트) → 압축 데이터(모든 파일을 이은 단일 solid 폴더) → 헤더.
/// 헤더 = MainStreamsInfo(PackInfo/UnpackInfo/SubStreamsInfo: 파일별 크기·CRC) + FilesInfo(UTF-16LE 이름,
/// EmptyStream/EmptyFile, MTime, Attributes). 암호화 폴더는 코더 [압축, 7zAES] 와 bind pair(in 0 ← out 1).
/// </summary>
internal static class SevenZipWriter
{
    private static readonly byte[] Signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04];
    private static readonly byte[] CopyId = [0x00];
    private static readonly byte[] LzmaId = [0x03, 0x01, 0x01];
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeArchive = 0x20;
    private const int BufferSize = 81920;

    // 7Z 헤더 속성 ID (7zFormat.txt)
    private const byte KEnd = 0x00, KHeader = 0x01, KMainStreamsInfo = 0x04, KFilesInfo = 0x05, KPackInfo = 0x06,
        KUnpackInfo = 0x07, KSubStreamsInfo = 0x08, KSize = 0x09, KCrc = 0x0A, KFolder = 0x0B, KCodersUnpackSize = 0x0C,
        KNumUnpackStream = 0x0D, KEmptyStream = 0x0E, KEmptyFile = 0x0F, KName = 0x11, KMTime = 0x14, KAttributes = 0x15,
        KEncodedHeader = 0x17;

    /// <summary>
    /// 압축 수준 → LZMA 사전 크기·fast bytes. 7-Zip 의 -mx 표를 따르되 관리 코드 인코더 메모리를 생각해 최대 64 MiB.
    /// 1: 256 KiB, 2: 1 MiB, 3: 4 MiB, 4: 8 MiB, 5·6: 16 MiB, 7·8: 32 MiB, 9: 64 MiB; fast bytes 는 1~6 = 32, 7~9 = 64.
    /// 입력이 사전보다 작으면 입력 크기(2의 거듭제곱, 최소 64 KiB)로 줄인다 — 결과는 같고 메모리만 아낀다.
    /// </summary>
    internal static (int Dictionary, int FastBytes) LzmaParameters(int level, long inputSize)
    {
        int dict = level switch
        {
            1 => 256 << 10,
            2 => 1 << 20,
            3 => 4 << 20,
            4 => 8 << 20,
            5 or 6 => 16 << 20,
            7 or 8 => 32 << 20,
            _ => 64 << 20,
        };
        long reduced = 64 << 10;
        while (reduced < inputSize && reduced < dict)
            reduced <<= 1;
        return ((int)Math.Min(dict, reduced), level >= 7 ? 64 : 32);
    }

    private sealed record Coder(byte[] Id, byte[]? Properties);

    private sealed record FileRecord(string Name, bool IsDirectory, bool HasStream, DateTime? ModifiedUtc);

    /// <summary>쓰기를 세는 래퍼. 안쪽 스트림을 닫지 않는다(코더 스트림이 Dispose 로 바깥을 닫지 못하게).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;

        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>압축(Copy|LZMA) → (AES) → 출력 으로 이어진 코더 사슬 하나 = 7Z 폴더 하나.</summary>
    private sealed class CoderChain
    {
        private readonly CountingStream _pack;
        private readonly LzmaStream? _lzma;
        private readonly AesEncryptStream? _aes;

        public Stream Input { get; }
        public List<Coder> Coders { get; } = [];
        public long DataSize { get; private set; }
        public long PackSize => _pack.Count;

        public CoderChain(Stream output, int level, long sizeHint, byte[]? key, byte[] salt)
        {
            _pack = new CountingStream(output);
            Stream target = _pack;
            byte[]? aesProps = null;
            if (key is not null)
            {
                byte[] iv = RandomNumberGenerator.GetBytes(SevenZipAes.IvSize);
                _aes = new AesEncryptStream(_pack, key, iv);
                aesProps = SevenZipAes.Properties(salt, iv);
                target = _aes;
            }
            if (level == 0)
            {
                Coders.Add(new Coder(CopyId, null));
                Input = new CountingStream(target);
            }
            else
            {
                var (dict, fb) = LzmaParameters(level, sizeHint);
                _lzma = new LzmaStream(new LzmaEncoderProperties(false, dict, fb), false, new CountingStream(target));
                Coders.Add(new Coder(LzmaId, _lzma.Properties));
                Input = _lzma;
            }
            if (aesProps is not null)
                Coders.Add(new Coder(SevenZipAes.CoderId, aesProps));
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            Input.Write(data);
            DataSize += data.Length;
        }

        public void Finish()
        {
            _lzma?.Dispose();
            if (_aes is not null)
            {
                _aes.Finish();
                _aes.Dispose();
            }
        }

        /// <summary>코더 출력 스트림 순서의 풀린 크기: [원본 크기, (AES 출력 = 압축 크기)].</summary>
        public long[] UnpackSizes => _aes is null ? [DataSize] : [DataSize, _aes.InputBytes];
    }

    /// <summary>헤더 바이트 조립기(7Z 가변 길이 NUMBER, 비트 벡터).</summary>
    private sealed class HeaderBuilder
    {
        private readonly MemoryStream _ms = new();

        public void Byte(byte b) => _ms.WriteByte(b);

        public void Bytes(ReadOnlySpan<byte> b) => _ms.Write(b);

        public void Number(ulong value)
        {
            byte first = 0;
            byte mask = 0x80;
            int i;
            for (i = 0; i < 8; i++)
            {
                if (value < (1UL << (7 * (i + 1))))
                {
                    first |= (byte)(value >> (8 * i));
                    break;
                }
                first |= mask;
                mask >>= 1;
            }
            _ms.WriteByte(first);
            for (; i > 0; i--)
            {
                _ms.WriteByte((byte)value);
                value >>= 8;
            }
        }

        public void Number(long value) => Number((ulong)value);

        public void UInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _ms.Write(b);
        }

        public void UInt64(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            _ms.Write(b);
        }

        public static byte[] BitVector(IReadOnlyList<bool> bits)
        {
            var bytes = new byte[(bits.Count + 7) / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            return bytes;
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>
    /// items 를 차례로 읽어 7Z 하나를 output 에 쓴다(output 은 탐색 가능해야 함: 끝에서 시작 헤더를 고쳐 쓴다).
    /// items 는 지연 열거여도 된다(고치기 때 기존 압축 파일을 풀면서 바로 다시 압축). totalBytes 는 진행률·사전 크기용 추정치.
    /// </summary>
    public static void Write(Stream output, IEnumerable<SevenZipItem> items, SevenZipWriteOptions options, long totalBytes,
        IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        long baseOffset = output.Position;
        output.Write(new byte[32]);
        byte[]? key = null;
        byte[] salt = [];
        if (!string.IsNullOrEmpty(options.Password))
        {
            salt = RandomNumberGenerator.GetBytes(SevenZipAes.SaltSize);
            key = SevenZipAes.DeriveKey(options.Password, salt);
        }
        try
        {
            var records = new List<FileRecord>();
            var substreams = new List<(long Size, uint Crc)>();
            CoderChain? chain = null;
            long processed = 0;
            byte[] buffer = new byte[BufferSize];
            foreach (var item in items)
            {
                cancel.ThrowIfCancellationRequested();
                if (item.IsDirectory)
                {
                    records.Add(new FileRecord(item.Name, true, false, item.ModifiedUtc));
                    continue;
                }
                var crc = new Crc32();
                long size = 0;
                using (var source = item.Open!())
                {
                    int n;
                    while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        chain ??= new CoderChain(output, options.Level, totalBytes, key, salt);
                        chain.Write(buffer.AsSpan(0, n));
                        crc.Append(buffer.AsSpan(0, n));
                        size += n;
                        processed += n;
                        progress?.Report(new ProgressInfo(processed, totalBytes, item.Name));
                    }
                }
                records.Add(new FileRecord(item.Name, false, size > 0, item.ModifiedUtc));
                if (size > 0)
                    substreams.Add((size, crc.Value));
            }
            cancel.ThrowIfCancellationRequested();
            chain?.Finish();

            byte[] header = records.Count == 0 ? [] : BuildHeader(records, chain, substreams);
            if (header.Length > 0 && options.EncryptNames && key is not null)
            {
                long headerPackPos = chain?.PackSize ?? 0;
                var headerChain = new CoderChain(output, Math.Max(options.Level, 1), header.Length, key, salt);
                headerChain.Write(header);
                headerChain.Finish();
                var crc = new Crc32();
                crc.Append(header);
                var h = new HeaderBuilder();
                h.Byte(KEncodedHeader);
                WriteStreamsInfo(h, headerPackPos, headerChain, crc.Value, null);
                header = h.ToArray();
            }

            long headerOffset = output.Position - baseOffset - 32;
            output.Write(header);
            long end = output.Position;
            var headerCrc = new Crc32();
            headerCrc.Append(header);

            byte[] start = new byte[32];
            Signature.CopyTo(start, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(12), (ulong)(header.Length == 0 ? 0 : headerOffset));
            BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(20), (ulong)header.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(28), header.Length == 0 ? 0 : headerCrc.Value);
            var startCrc = new Crc32();
            startCrc.Append(start.AsSpan(12, 20));
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(8), startCrc.Value);
            output.Position = baseOffset;
            output.Write(start);
            output.Position = end;
            output.Flush();
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void WriteStreamsInfo(HeaderBuilder h, long packPos, CoderChain chain, uint? folderCrc,
        IReadOnlyList<(long Size, uint Crc)>? substreams)
    {
        h.Byte(KPackInfo);
        h.Number(packPos);
        h.Number(1);
        h.Byte(KSize);
        h.Number(chain.PackSize);
        h.Byte(KEnd);

        h.Byte(KUnpackInfo);
        h.Byte(KFolder);
        h.Number(1);
        h.Byte(0); // External = 0
        h.Number(chain.Coders.Count);
        foreach (var coder in chain.Coders)
        {
            h.Byte((byte)(coder.Id.Length | (coder.Properties is null ? 0 : 0x20)));
            h.Bytes(coder.Id);
            if (coder.Properties is not null)
            {
                h.Number(coder.Properties.Length);
                h.Bytes(coder.Properties);
            }
        }
        // bind pair: 코더 i 의 입력 ← 코더 i+1 의 출력. 남는 입력(마지막 코더)이 압축 스트림 하나라 인덱스는 적지 않는다.
        for (int i = 0; i < chain.Coders.Count - 1; i++)
        {
            h.Number(i);
            h.Number(i + 1);
        }
        h.Byte(KCodersUnpackSize);
        foreach (long size in chain.UnpackSizes)
            h.Number(size);
        if (folderCrc is { } fc)
        {
            h.Byte(KCrc);
            h.Byte(1);
            h.UInt32(fc);
        }
        h.Byte(KEnd);

        if (substreams is not null)
        {
            h.Byte(KSubStreamsInfo);
            if (substreams.Count != 1)
            {
                h.Byte(KNumUnpackStream);
                h.Number(substreams.Count);
            }
            if (substreams.Count > 1)
            {
                h.Byte(KSize);
                for (int i = 0; i < substreams.Count - 1; i++)
                    h.Number(substreams[i].Size);
            }
            h.Byte(KCrc);
            h.Byte(1);
            foreach (var s in substreams)
                h.UInt32(s.Crc);
            h.Byte(KEnd);
        }
        h.Byte(KEnd);
    }

    private static byte[] BuildHeader(List<FileRecord> records, CoderChain? chain, List<(long Size, uint Crc)> substreams)
    {
        var h = new HeaderBuilder();
        h.Byte(KHeader);
        if (chain is not null)
        {
            h.Byte(KMainStreamsInfo);
            WriteStreamsInfo(h, 0, chain, null, substreams);
        }

        h.Byte(KFilesInfo);
        h.Number(records.Count);

        var emptyStream = records.Select(r => !r.HasStream).ToList();
        if (emptyStream.Any(e => e))
        {
            byte[] bits = HeaderBuilder.BitVector(emptyStream);
            h.Byte(KEmptyStream);
            h.Number(bits.Length);
            h.Bytes(bits);
            var emptyFile = records.Where(r => !r.HasStream).Select(r => !r.IsDirectory).ToList();
            if (emptyFile.Any(e => e))
            {
                bits = HeaderBuilder.BitVector(emptyFile);
                h.Byte(KEmptyFile);
                h.Number(bits.Length);
                h.Bytes(bits);
            }
        }

        var names = new MemoryStream();
        names.WriteByte(0); // External = 0
        foreach (var r in records)
        {
            names.Write(Encoding.Unicode.GetBytes(r.Name));
            names.Write([0, 0]);
        }
        h.Byte(KName);
        h.Number(names.Length);
        h.Bytes(names.ToArray());

        var defined = records.Select(r => r.ModifiedUtc is not null).ToList();
        int definedCount = defined.Count(d => d);
        if (definedCount > 0)
        {
            bool all = definedCount == records.Count;
            byte[] bits = all ? [] : HeaderBuilder.BitVector(defined);
            h.Byte(KMTime);
            h.Number(1 + bits.Length + 1 + 8L * definedCount);
            h.Byte((byte)(all ? 1 : 0));
            h.Bytes(bits);
            h.Byte(0); // External = 0
            foreach (var r in records)
            {
                if (r.ModifiedUtc is { } t)
                    h.UInt64((ulong)ToFileTime(t));
            }
        }

        h.Byte(KAttributes);
        h.Number(2 + 4L * records.Count);
        h.Byte(1); // AllAreDefined
        h.Byte(0); // External = 0
        foreach (var r in records)
            h.UInt32(r.IsDirectory ? AttributeDirectory : AttributeArchive);

        h.Byte(KEnd);
        h.Byte(KEnd);
        return h.ToArray();
    }

    private static long ToFileTime(DateTime utc)
    {
        try
        {
            return utc.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
