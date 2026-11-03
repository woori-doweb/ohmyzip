using System.Buffers.Binary;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OhMyZip.Core;

/// <summary>
/// TAR · TAR.GZ · GZ 엔진(쓰기는 BCL System.Formats.Tar, 읽기는 자체 TAR 헤더 해석 + GZipStream/DeflateStream). 목록·검사·해제는 세 형식 모두,
/// 만들기는 TAR/TAR.GZ(PAX 형식: UTF-8 경로, 긴 이름) 와 GZ(파일 하나만), 고치기는 TAR/TAR.GZ 를 임시 사본에 다시 써서 바꿔 넣는다.
/// 암호화·분할·설명은 지원하지 않는다.
/// </summary>
public sealed class TarEngine : IArchiveEngine
{
    private const int BufferSize = 81920;
    private readonly ArchiveFormat _format;
    private readonly Encoding? _forcedEncoding;

    public TarEngine(ArchiveFormat format, ReadOptions? read = null)
    {
        if (format is not (ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.GZip))
            throw new ArgumentOutOfRangeException(nameof(format));
        _format = format;
        // 암호는 TAR/GZ 에 해당하지 않는다. 코드페이지는 PAX 가 아닌 TAR 이름에만 쓴다(PAX 경로는 UTF-8 로 정해져 있다).
        _forcedEncoding = read?.CodePage is { } cp ? Encoding.GetEncoding(cp) : null;
        bool tar = format != ArchiveFormat.GZip;
        Capabilities = new FormatCapabilities(Read: true, Test: true, Extract: true, Create: true, Modify: tar, Encrypt: false, Split: false, Comment: false);
    }

    public FormatCapabilities Capabilities { get; }

    private string FormatName => _format switch
    {
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.TarGz => "TAR.GZ",
        _ => "GZ",
    };

    private static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

    /// <summary>
    /// 읽기만 넘기고 Dispose 로 안쪽을 닫지 않는 래퍼. 읽을 때마다 취소를 확인하고 읽은 양을 알린다.
    /// length 를 주면 TarWriter 가 헤더에 크기를 먼저 쓸 수 있도록 길이·위치를 알려 준다(앞으로만 읽고, 현재 위치 밖 탐색은 못 함).
    /// </summary>
    private sealed class ReadThrough(Stream inner, CancellationToken cancel, Action<int>? onRead = null, long? length = null) : Stream
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancel.ThrowIfCancellationRequested();
            int n = inner.Read(buffer);
            if (n > 0)
            {
                _read += n;
                onRead?.Invoke(n);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => length is not null;
        public override bool CanWrite => false;
        public override long Length => length ?? throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _read + offset,
                _ => Length + offset,
            };
            return target == _read ? _read : throw new NotSupportedException("앞으로만 읽는 스트림입니다");
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- GZ 머리·꼬리 ----------

    /// <summary>GZ 머리(RFC 1952)의 원래 이름(FNAME)·수정 시각과 꼬리의 CRC-32·원래 크기(mod 2^32).</summary>
    private sealed record GZipInfo(string? FileName, DateTimeOffset? Modified, uint Crc, uint Size, long CompressedSize);

    private static GZipInfo ReadGZipInfo(string path)
    {
        using var fs = OpenFile(path);
        Span<byte> head = stackalloc byte[10];
        if (fs.ReadAtLeast(head, 10, throwOnEndOfStream: false) < 10 || head[0] != 0x1F || head[1] != 0x8B || head[2] != 8)
            throw new InvalidDataException("GZ 머리가 올바르지 않습니다");
        byte flags = head[3];
        uint mtime = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        if ((flags & 0x04) != 0)
        {
            Span<byte> xlen = stackalloc byte[2];
            fs.ReadExactly(xlen);
            fs.Seek(BinaryPrimitives.ReadUInt16LittleEndian(xlen), SeekOrigin.Current);
        }
        string? name = null;
        if ((flags & 0x08) != 0)
            name = DecodeName(ReadZeroTerminated(fs));
        if (fs.Length < 18)
            throw new InvalidDataException("GZ 파일이 너무 짧습니다");
        fs.Seek(-8, SeekOrigin.End);
        Span<byte> tail = stackalloc byte[8];
        fs.ReadExactly(tail);
        return new GZipInfo(name, mtime == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(mtime),
            BinaryPrimitives.ReadUInt32LittleEndian(tail), BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]), fs.Length);
    }

    private static byte[] ReadZeroTerminated(Stream s)
    {
        var bytes = new List<byte>();
        int b;
        while ((b = s.ReadByte()) > 0)
        {
            bytes.Add((byte)b);
            if (bytes.Count > 4096)
                throw new InvalidDataException("GZ 이름이 너무 깁니다");
        }
        if (b < 0)
            throw new InvalidDataException("GZ 머리가 잘렸습니다");
        return [.. bytes];
    }

    /// <summary>FNAME 은 규격상 ISO-8859-1 이지만 실제로는 UTF-8 이 많다: 올바른 UTF-8 이면 UTF-8, 아니면 Latin-1.</summary>
    private static string DecodeName(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>GZ 안 파일 이름: FNAME 의 마지막 경로 조각, 없으면 압축파일 이름에서 .gz 를 뺀 것.</summary>
    private static string GZipEntryName(string archivePath, GZipInfo info)
    {
        string? name = info.FileName is { } n ? SafePaths.ToDisplayPath(n).Split('/')[^1] : null;
        if (!string.IsNullOrEmpty(name))
            return name;
        string file = Path.GetFileName(archivePath);
        return file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && file.Length > 3 ? file[..^3] : Path.GetFileNameWithoutExtension(file);
    }

    // ---------- TAR 읽기 ----------

    private enum Kind { Directory, File, Link, Other }

    /// <summary>
    /// TAR 항목 하나. 메타 헤더(PAX 'x' 와 전역 'g', GNU 긴 이름 'L'·'K')는 읽는 쪽에서 흡수해 이름·크기·시각에 반영한다.
    /// Data 는 다음 항목으로 넘어가기 전까지만 읽을 수 있다.
    /// </summary>
    private sealed class RawTarEntry
    {
        public required string Name { get; init; }
        public required char Type { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Modified { get; init; }
        public string LinkName { get; init; } = "";
        public int Mode { get; init; }
        public int Uid { get; init; }
        public int Gid { get; init; }
        public string UserName { get; init; } = "";
        public string GroupName { get; init; } = "";
        public Stream Data { get; init; } = Stream.Null;

        public Kind Kind => Type switch
        {
            '5' => Kind.Directory,
            '0' or '\0' or '7' when Name.EndsWith('/') => Kind.Directory, // 옛 TAR: 이름 끝 '/' 가 폴더
            '0' or '\0' or '7' => Kind.File,
            '1' or '2' => Kind.Link,
            _ => Kind.Other,
        };
    }

    /// <summary>
    /// 앞으로만 읽는 TAR 읽기(ustar·GNU·PAX·옛 v7). 헤더 체크섬을 확인하고(TAR 에는 데이터 체크섬이 없어 유일한 무결성 정보),
    /// 이름은 PAX path(UTF-8) → GNU 긴 이름 → ustar prefix/name 순서로 정한다. PAX 가 아닌 이름 바이트는
    /// forced 코드페이지가 있으면 그것으로, 없으면 올바른 UTF-8 이면 UTF-8, 아니면 CP949(한국어 Windows 도구가 만든 TAR)로 읽는다.
    /// </summary>
    private sealed class TarStreamReader(Stream stream, Encoding? forced)
    {
        private readonly byte[] _block = new byte[512];
        private long _pending; // 현재 항목에서 아직 읽지 않은 데이터 + 채움 바이트

        public RawTarEntry? Next(CancellationToken cancel)
        {
            Skip(stream, _pending);
            _pending = 0;
            Dictionary<string, string>? pax = null;
            byte[]? longName = null;
            byte[]? longLink = null;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (stream.ReadAtLeast(_block, 512, throwOnEndOfStream: false) < 512)
                    throw new InvalidDataException("TAR 가 잘렸습니다 (끝 블록 없음)");
                if (_block.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                    return null;
                VerifyChecksum(_block);
                char type = (char)_block[156];
                long size = ParseSize(_block.AsSpan(124, 12));
                switch (type)
                {
                    case 'x':
                        pax ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (k, v) in ParsePax(ReadMeta(size)))
                            pax[k] = v;
                        continue;
                    case 'g':
                        ReadMeta(size); // 전역 속성은 쓰지 않는다
                        continue;
                    case 'L':
                        longName = ReadMeta(size);
                        continue;
                    case 'K':
                        longLink = ReadMeta(size);
                        continue;
                }
                bool ustar = _block.AsSpan(257, 5).SequenceEqual("ustar"u8);
                string name;
                if (pax is not null && pax.TryGetValue("path", out string? paxPath))
                {
                    name = paxPath;
                }
                else if (longName is not null)
                {
                    name = Decode(longName);
                }
                else
                {
                    name = Decode(_block.AsSpan(0, 100));
                    string prefix = ustar ? Decode(_block.AsSpan(345, 155)) : "";
                    if (prefix.Length > 0)
                        name = prefix + "/" + name;
                }
                string link = pax is not null && pax.TryGetValue("linkpath", out string? paxLink) ? paxLink
                    : longLink is not null ? Decode(longLink) : Decode(_block.AsSpan(157, 100));
                if (pax is not null && pax.TryGetValue("size", out string? paxSize) && long.TryParse(paxSize, out long s))
                    size = s;
                DateTimeOffset? modified = pax is not null && pax.TryGetValue("mtime", out string? paxTime)
                    && decimal.TryParse(paxTime, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal t)
                    ? FromUnix((long)Math.Floor(t))
                    : FromUnix(ParseOctal(_block.AsSpan(136, 12)));
                // 링크·장치·폴더·FIFO 의 크기 필드는 데이터 길이가 아니다.
                long dataLength = type is '1' or '2' or '3' or '4' or '5' or '6' ? 0 : size;
                if (dataLength < 0)
                    throw new InvalidDataException("TAR 항목 크기가 올바르지 않습니다");
                _pending = (dataLength + 511) / 512 * 512;
                return new RawTarEntry
                {
                    Name = name,
                    Type = type,
                    Size = dataLength,
                    Modified = modified,
                    LinkName = link,
                    Mode = (int)ParseOctal(_block.AsSpan(100, 8)),
                    Uid = (int)ParseSize(_block.AsSpan(108, 8)),
                    Gid = (int)ParseSize(_block.AsSpan(116, 8)),
                    UserName = ustar ? Decode(_block.AsSpan(265, 32)) : "",
                    GroupName = ustar ? Decode(_block.AsSpan(297, 32)) : "",
                    Data = new EntryData(this, dataLength),
                };
            }
        }

        /// <summary>메타 헤더의 데이터(최대 1 MiB)를 채움 바이트까지 읽는다.</summary>
        private byte[] ReadMeta(long size)
        {
            if (size is < 0 or > 1 << 20)
                throw new InvalidDataException("TAR 확장 헤더가 너무 큽니다");
            byte[] data = new byte[size];
            stream.ReadExactly(data);
            Skip(stream, (512 - size % 512) % 512);
            return data;
        }

        private string Decode(ReadOnlySpan<byte> field)
        {
            int nul = field.IndexOf((byte)0);
            if (nul >= 0)
                field = field[..nul];
            if (forced is not null)
                return forced.GetString(field);
            try
            {
                return StrictUtf8.GetString(field);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(949).GetString(field);
            }
        }

        private static DateTimeOffset? FromUnix(long seconds)
        {
            try
            {
                return seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private int ReadData(Span<byte> buffer)
        {
            int n = stream.Read(buffer);
            if (n > 0)
                _pending -= n;
            return n;
        }

        /// <summary>현재 항목 데이터만 읽게 막는 스트림. 끝 전에 바탕 스트림이 끝나면 잘린 것으로 본다.</summary>
        private sealed class EntryData(TarStreamReader owner, long length) : Stream
        {
            private readonly long _length = length;
            private long _left = length;

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_left == 0 || buffer.Length == 0)
                    return 0;
                int n = owner.ReadData(buffer[..(int)Math.Min(buffer.Length, _left)]);
                if (n <= 0)
                    throw new InvalidDataException("TAR 항목 데이터가 잘렸습니다");
                _left -= n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _length - _left;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static TarEngine() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>체크섬 필드를 공백으로 본 바이트 합(부호 없는/있는 합 모두 허용).</summary>
    private static void VerifyChecksum(byte[] block)
    {
        long unsignedSum = 0, signedSum = 0;
        for (int i = 0; i < 512; i++)
        {
            byte b = i is >= 148 and < 156 ? (byte)' ' : block[i];
            unsignedSum += b;
            signedSum += (sbyte)b;
        }
        long stored = ParseOctal(block.AsSpan(148, 8));
        if (stored != unsignedSum && stored != signedSum)
            throw new InvalidDataException("TAR 헤더 체크섬 불일치");
    }

    private static void Skip(Stream s, long count)
    {
        byte[] buffer = new byte[BufferSize];
        while (count > 0)
        {
            int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0)
                throw new InvalidDataException("TAR 항목 데이터가 잘렸습니다");
            count -= n;
        }
    }

    private static long ParseOctal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (byte b in field)
        {
            if (b is (byte)' ' or 0)
            {
                if (value != 0)
                    break;
                continue;
            }
            if (b is < (byte)'0' or > (byte)'7')
                throw new InvalidDataException("TAR 헤더의 숫자 필드가 올바르지 않습니다");
            value = checked(value * 8 + (b - '0'));
        }
        return value;
    }

    /// <summary>숫자 필드: 8진수 또는 GNU base-256(첫 바이트 0x80).</summary>
    private static long ParseSize(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) == 0)
            return ParseOctal(field);
        long value = field[0] & 0x7F;
        foreach (byte b in field[1..])
            value = checked(value * 256 + b);
        return value;
    }

    /// <summary>PAX 레코드 "길이 키=값\n" 목록(UTF-8).</summary>
    private static List<(string Key, string Value)> ParsePax(byte[] data)
    {
        var records = new List<(string, string)>();
        int pos = 0;
        while (pos < data.Length)
        {
            int space = Array.IndexOf(data, (byte)' ', pos);
            if (space < 0 || !int.TryParse(Encoding.ASCII.GetString(data, pos, space - pos), out int length)
                || length <= space - pos + 1 || pos + length > data.Length)
            {
                throw new InvalidDataException("PAX 확장 헤더가 올바르지 않습니다");
            }
            string record = Encoding.UTF8.GetString(data, space + 1, pos + length - space - 2);
            int eq = record.IndexOf('=');
            if (eq > 0)
                records.Add((record[..eq], record[(eq + 1)..]));
            pos += length;
        }
        return records;
    }

    private Stream OpenTarStream(string archivePath)
    {
        var fs = OpenFile(archivePath);
        return _format == ArchiveFormat.TarGz ? new GZipStream(fs, CompressionMode.Decompress) : fs;
    }

    /// <summary>TAR 항목을 차례로 돌며 visit 를 부른다(Data 는 다음 항목으로 넘어가기 전까지만 유효).</summary>
    private void ReadTar(string archivePath, CancellationToken cancel, Action<RawTarEntry> visit)
    {
        using var stream = OpenTarStream(archivePath);
        var reader = new TarStreamReader(stream, _forcedEncoding);
        while (reader.Next(cancel) is { } e)
            visit(e);
        if (_format == ArchiveFormat.TarGz)
        {
            // 끝 블록 뒤 남은 압축 데이터까지 읽어 GZ 꼬리(CRC·크기) 검증이 끝나게 한다.
            stream.CopyTo(Stream.Null);
        }
    }

    public ListResult List(string archivePath, CancellationToken cancel = default)
    {
        try
        {
            if (_format == ArchiveFormat.GZip)
            {
                var info = ReadGZipInfo(archivePath);
                var entry = new ArchiveEntry(GZipEntryName(archivePath, info), false, info.Size, info.CompressedSize, info.Modified, info.Crc, "Deflate", false);
                return new ListResult(OperationResult.Ok("항목 1개"), [entry]);
            }
            var entries = new List<ArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ReadTar(archivePath, cancel, e =>
            {
                string path = SafePaths.ToDisplayPath(e.Name);
                if (path.Length == 0)
                    return;
                bool dir = e.Kind == Kind.Directory;
                if (seen.Add(path))
                    entries.Add(new ArchiveEntry(path, dir, e.Size, e.Size, e.Modified, null, null, false));
            });
                // DEF-02: 목록은 압축파일에 기록된 항목만 돌려준다(경로로만 생기는 폴더는 앱이 트리에서 만든다).
            return new ListResult(OperationResult.Ok($"항목 {entries.Count}개"), entries);
        }
        catch (OperationCanceledException)
        {
            return new ListResult(OperationResult.Cancelled(), []);
        }
        catch (Exception ex)
        {
            return new ListResult(OperationResult.Fail($"{FormatName} 압축파일을 열 수 없습니다", ex.Message), []);
        }
    }

    public OperationResult Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        try
        {
            if (_format == ArchiveFormat.GZip)
                return TestGZip(archivePath, progress, cancel);
            long processed = 0;
            int files = 0;
            ReadTar(archivePath, cancel, e =>
            {
                if (e.Kind != Kind.File)
                    return;
                files++;
                string path = SafePaths.ToDisplayPath(e.Name);
                long size = 0;
                new ReadThrough(e.Data, cancel, n =>
                {
                    size += n;
                    processed += n;
                    progress?.Report(new ProgressInfo(processed, 0, path));
                }).CopyTo(Stream.Null);
                if (size != e.Size)
                    throw new InvalidDataException($"{path}: 크기 불일치 (기대 {e.Size}, 실제 {size})");
            });
            return OperationResult.Ok($"파일 {files}개 이상 없음");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{FormatName} 압축파일이 손상되었습니다", ex.Message);
        }
    }

    /// <summary>끝까지 풀어 CRC-32 와 크기(mod 2^32)를 꼬리 값과 비교한다(한 멤버 GZ 기준).</summary>
    private static OperationResult TestGZip(string archivePath, IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        var info = ReadGZipInfo(archivePath);
        string name = GZipEntryName(archivePath, info);
        var crc = new Crc32();
        long size = 0;
        using (var fs = OpenFile(archivePath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            byte[] buffer = new byte[BufferSize];
            int n;
            while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, n));
                size += n;
                progress?.Report(new ProgressInfo(size, info.Size, name));
            }
        }
        if (crc.Value != info.Crc)
            return OperationResult.Fail("GZ 압축파일이 손상되었습니다", $"{name}: CRC 불일치 (기대 {info.Crc:X8}, 실제 {crc.Value:X8})");
        if ((uint)size != info.Size)
            return OperationResult.Fail("GZ 압축파일이 손상되었습니다", $"{name}: 크기 불일치 (기대 {info.Size}, 실제 {(uint)size})");
        return OperationResult.Ok("파일 1개 이상 없음");
    }

    public OperationResult Extract(string archivePath, string destinationDirectory, ExtractOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        int count;
        long total;
        GZipInfo? gzip = null;
        try
        {
            if (_format == ArchiveFormat.GZip)
            {
                gzip = ReadGZipInfo(archivePath);
                count = ExtractSession.IsSelected(options, GZipEntryName(archivePath, gzip)) ? 1 : 0;
                total = gzip.Size;
            }
            else
            {
                // 헤더 체크섬을 확인하고, 항목 수 한도·진행률 분모를 위해 먼저 한 번 훑는다.
                int c = 0;
                long t = 0;
                ReadTar(archivePath, cancel, e =>
                {
                    if (!ExtractSession.IsSelected(options, e.Name))
                        return;
                    c++;
                    if (e.Kind == Kind.File)
                        t += e.Size;
                });
                count = c;
                total = t;
            }
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{FormatName} 압축파일을 열 수 없습니다", ex.Message);
        }

        var begin = ExtractSession.TryBegin(destinationDirectory, options, count, progress, cancel, total, out var session);
        if (begin is not null)
            return begin;
        try
        {
            if (gzip is not null)
            {
                string name = GZipEntryName(archivePath, gzip);
                if (count > 0)
                {
                    session.AddFile(name, false, gzip.Crc, gzip.Modified,
                        () => new GZipStream(OpenFile(archivePath), CompressionMode.Decompress));
                }
            }
            else
            {
                ReadTar(archivePath, cancel, e =>
                {
                    if (!ExtractSession.IsSelected(options, e.Name))
                        return;
                    switch (e.Kind)
                    {
                        case Kind.Directory:
                            session.AddDirectory(e.Name, false);
                            break;
                        case Kind.File:
                            var data = e.Data;
                            session.AddFile(e.Name, false, null, e.Modified, () => new ReadThrough(data, cancel));
                            break;
                        case Kind.Link:
                            session.AddFile(e.Name, true, null, null, () => new MemoryStream());
                            break;
                        default:
                            session.Reject(e.Name, $"지원하지 않는 항목 종류(TAR 형식 '{e.Type}')");
                            break;
                    }
                });
            }
            return session.Complete();
        }
        catch (OperationCanceledException)
        {
            return session.Rollback(OperationResult.Cancelled("해제를 취소했습니다. 이번에 만든 파일을 지웠습니다"));
        }
        catch (ExtractLimitException ex)
        {
            return session.Rollback(OperationResult.Fail(ex.Message, ex.Message));
        }
        catch (Exception ex)
        {
            return session.Rollback(OperationResult.Fail("해제 중 오류로 중단했습니다", ex.Message));
        }
    }

    // ---------- 쓰기 ----------

    /// <summary>
    /// GZ 압축 수준 → BCL CompressionLevel: 0 = NoCompression, 1~3 = Fastest, 4~6 = Optimal, 7~9 = SmallestSize.
    /// </summary>
    private static CompressionLevel GZipLevel(int level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    /// <summary>쓰기 상태: 진행률 누계와 취소.</summary>
    private sealed class WriteProgress(long total, IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        private long _processed;
        public CancellationToken Cancel => cancel;

        public Stream Wrap(Stream source, string name, long length) =>
            new ReadThrough(source, cancel, length: length, onRead: n =>
            {
                _processed += n;
                progress?.Report(new ProgressInfo(_processed, total, name));
            });
    }

    private static void WriteSource(TarWriter writer, SourceEntry s, WriteProgress wp)
    {
        wp.Cancel.ThrowIfCancellationRequested();
        var time = SourceTree.ModifiedUtc(s) is { } t ? new DateTimeOffset(t) : DateTimeOffset.UtcNow;
        if (s.IsDirectory)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, s.EntryName + "/")
            {
                ModificationTime = time,
                Mode = (UnixFileMode)0b111_101_101,
            });
            return;
        }
        using var fs = new FileStream(s.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, s.EntryName)
        {
            ModificationTime = time,
            Mode = (UnixFileMode)0b110_100_100,
            DataStream = wp.Wrap(fs, s.EntryName, fs.Length),
        });
    }

    /// <summary>TAR(.GZ) 쓰기 스트림을 연다. 돌려준 스트림을 닫으면 GZ 꼬리까지 쓰인다.</summary>
    private Stream OpenTarOutput(FileStream fs, int level) =>
        _format == ArchiveFormat.TarGz ? new GZipStream(fs, GZipLevel(level), leaveOpen: true) : new NonClosingWriteStream(fs);

    /// <summary>FileStream 을 닫지 않고 쓰기만 넘기는 래퍼. TAR 도 TAR.GZ 처럼 "출력 스트림을 닫아도 파일은 열린 채"로 다루려고 쓴다.</summary>
    private sealed class NonClosingWriteStream(Stream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        options ??= new CreateOptions { Format = _format };
        if (options.Format != _format)
            return OperationResult.Unsupported($"{FormatName} 엔진은 {FormatName} 만 만듭니다");
        if (options.Level is < 0 or > 9)
            return OperationResult.Fail("압축 수준은 0~9 사이여야 합니다", options.Level.ToString());
        if (!string.IsNullOrEmpty(options.Password) || options.Encryption != EncryptionMethod.None || options.EncryptNames)
            return OperationResult.Unsupported($"{FormatName} 는 암호화를 지원하지 않습니다");
        if (options.VolumeSize is not null)
            return OperationResult.Unsupported($"{FormatName} 는 분할 압축을 지원하지 않습니다");
        // TAR 는 압축하지 않는다: 기본값(5)과 0(저장)만 받고 다른 수준은 조용히 무시하지 않는다.
        if (_format == ArchiveFormat.Tar && options.Level is not (0 or 5))
            return OperationResult.Unsupported("TAR 는 압축하지 않으므로 압축 수준을 지정할 수 없습니다 (TAR.GZ 를 쓰세요)");

        string full;
        try
        {
            full = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축파일 경로가 올바르지 않습니다", ex.Message);
        }
        if (File.Exists(full) || Directory.Exists(full))
            return OperationResult.Fail("같은 이름의 파일이 이미 있습니다", full);
        if (sources.Count == 0)
            return OperationResult.Fail("압축할 파일이 없습니다");
        if (!Directory.Exists(Path.GetDirectoryName(full)))
            return OperationResult.Fail("압축파일을 둘 폴더가 없습니다", Path.GetDirectoryName(full) ?? full);
        if (_format == ArchiveFormat.GZip && (sources.Count != 1 || !File.Exists(sources[0])))
            return OperationResult.Unsupported("GZ 는 파일 하나만 압축할 수 있습니다. 여러 파일·폴더는 TAR.GZ 를 쓰세요");

        string temp = AtomicFile.TempSibling(full);
        var problems = new List<string>();
        var items = SourceTree.Expand(sources, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full, temp }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "압축할 원본을 찾을 수 없습니다", problems);
        if (SourceTree.DuplicateName(items) is { } dup)
            return OperationResult.Fail("원본 이름이 겹칩니다", dup);
        var wp = new WriteProgress(SourceTree.TotalBytes(items), progress, cancel);
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (_format == ArchiveFormat.GZip)
                {
                    WriteGZip(fs, items[0], options.Level, wp);
                }
                else
                {
                    using var output = OpenTarOutput(fs, options.Level);
                    using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
                    foreach (var item in items)
                        WriteSource(writer, item, wp);
                }
            }
            cancel.ThrowIfCancellationRequested();
            AtomicFile.Commit(temp, full, overwrite: false);
            return OperationResult.Ok($"항목 {items.Count}개로 압축파일을 만들었습니다");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("압축을 취소했습니다");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("압축파일을 만들지 못했습니다", ex.Message);
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    /// <summary>
    /// GZ 한 멤버: 머리(FNAME, MTIME) + Deflate + 꼬리(CRC-32, 크기 mod 2^32).
    /// FNAME 은 규격상 ISO-8859-1 이고 도구마다 해석이 달라(예: 시스템 코드페이지로 읽음) ASCII 이름일 때만 쓴다.
    /// 그 밖의 이름은 빼고, 읽는 쪽은 압축파일 이름에서 .gz 를 뛴 이름을 쓴다.
    /// </summary>
    private static void WriteGZip(Stream output, SourceEntry item, int level, WriteProgress wp)
    {
        string fileName = Path.GetFileName(item.FilePath!);
        byte[]? name = Ascii.IsValid(fileName) ? Encoding.ASCII.GetBytes(fileName) : null;
        uint mtime = SourceTree.ModifiedUtc(item) is { } t && t > DateTime.UnixEpoch
            ? (uint)Math.Min(uint.MaxValue, (long)(t - DateTime.UnixEpoch).TotalSeconds)
            : 0;
        byte[] head = new byte[10];
        head[0] = 0x1F;
        head[1] = 0x8B;
        head[2] = 8;      // Deflate
        head[3] = (byte)(name is null ? 0 : 0x08); // FNAME
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), mtime);
        head[8] = (byte)(level >= 7 ? 2 : level is > 0 and <= 3 ? 4 : 0);
        head[9] = 0xFF;   // OS: 알 수 없음
        output.Write(head);
        if (name is not null)
        {
            output.Write(name);
            output.WriteByte(0);
        }

        var crc = new Crc32();
        long size = 0;
        using (var source = new FileStream(item.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
        using (var deflate = new DeflateStream(output, GZipLevel(level), leaveOpen: true))
        {
            var counted = wp.Wrap(source, item.EntryName, source.Length);
            byte[] buffer = new byte[BufferSize];
            int n;
            while ((n = counted.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc.Append(buffer.AsSpan(0, n));
                size += n;
                deflate.Write(buffer, 0, n);
            }
        }
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, crc.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(tail[4..], (uint)size);
        output.Write(tail);
    }

    // ---------- 고치기(TAR/TAR.GZ 다시 쓰기) ----------

    private sealed record ArchiveIndex(HashSet<string> All, HashSet<string> Folders);

    private ArchiveIndex Index(string archivePath, CancellationToken cancel)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        ReadTar(archivePath, cancel, e =>
        {
            string p = SafePaths.ToDisplayPath(e.Name);
            if (p.Length == 0)
                return;
            all.Add(p);
            if (e.Kind == Kind.Directory)
                folders.Add(p);
            foreach (string parent in SafePaths.Parents(p))
            {
                all.Add(parent);
                folders.Add(parent);
            }
        });
        return new ArchiveIndex(all, folders);
    }

    /// <summary>기존 항목을 map(null = 버림)대로 옮겨 쓰고 extra 를 덧붙인 새 TAR(.GZ) 를 임시 파일에 써서, 성공했을 때만 원본과 바꾼다.</summary>
    private OperationResult Rewrite(string archivePath, Func<ArchiveIndex, OperationResult?> validate, Func<string, string?> map,
        Func<ArchiveIndex, IEnumerable<SourceEntry>> extra, string successMessage, IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        if (_format == ArchiveFormat.GZip)
            return OperationResult.Unsupported("GZ 는 파일 하나만 담으므로 고칠 수 없습니다");
        string full;
        ArchiveIndex index;
        try
        {
            full = Path.GetFullPath(archivePath);
            if (!File.Exists(full))
                return OperationResult.Fail("압축파일이 없습니다", archivePath);
            index = Index(full, cancel);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("취소했습니다. 압축파일은 바뀌지 않았습니다");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{FormatName} 압축파일을 열 수 없습니다", ex.Message);
        }
        if (validate(index) is { } invalid)
            return invalid;
        var added = extra(index).ToList();

        string temp = AtomicFile.TempSibling(full);
        try
        {
            long total = new FileInfo(full).Length + SourceTree.TotalBytes(added);
            var wp = new WriteProgress(total, progress, cancel);
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var output = OpenTarOutput(fs, 5);
                using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
                ReadTar(full, cancel, e =>
                {
                    string path = SafePaths.ToDisplayPath(e.Name);
                    if (path.Length == 0 || map(path) is not { } target)
                        return;
                    writer.WriteEntry(CopyEntry(e, e.Kind == Kind.Directory ? target + "/" : target, wp));
                });
                foreach (var item in added)
                    WriteSource(writer, item, wp);
            }
            cancel.ThrowIfCancellationRequested();
            AtomicFile.Commit(temp, full, overwrite: true);
            return OperationResult.Ok(successMessage);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("취소했습니다. 압축파일은 바뀌지 않았습니다");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("압축파일을 고치지 못했습니다. 원본은 바뀌지 않았습니다", ex.Message);
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    /// <summary>
    /// 기존 항목을 새 이름의 PAX 항목으로 옮긴다(종류·시각·권한·소유자·링크 대상). 그 밖의 PAX 확장 속성과 전역 속성은 옮기지 않는다.
    /// 옮길 수 없는 종류(GNU 희소 파일 등)를 만나면 예외 → 다시 쓰기 전체가 실패하고 원본은 그대로다.
    /// </summary>
    private static PaxTarEntry CopyEntry(RawTarEntry e, string name, WriteProgress wp)
    {
        var type = e.Kind switch
        {
            Kind.Directory => TarEntryType.Directory,
            Kind.File => TarEntryType.RegularFile,
            _ => e.Type switch
            {
                '1' => TarEntryType.HardLink,
                '2' => TarEntryType.SymbolicLink,
                '3' => TarEntryType.CharacterDevice,
                '4' => TarEntryType.BlockDevice,
                '6' => TarEntryType.Fifo,
                _ => throw new InvalidDataException($"옮길 수 없는 TAR 항목 종류 '{e.Type}': {e.Name}"),
            },
        };
        var copy = new PaxTarEntry(type, name)
        {
            ModificationTime = e.Modified ?? DateTimeOffset.UnixEpoch,
            Mode = (UnixFileMode)(e.Mode & 0xFFF),
            Uid = e.Uid,
            Gid = e.Gid,
            UserName = e.UserName,
            GroupName = e.GroupName,
        };
        if (e.Kind == Kind.Link)
            copy.LinkName = e.LinkName;
        if (e.Kind == Kind.File)
            copy.DataStream = wp.Wrap(e.Data, name, e.Size);
        return copy;
    }

    public OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        if (_format == ArchiveFormat.GZip)
            return OperationResult.Unsupported("GZ 는 파일 하나만 담으므로 고칠 수 없습니다");
        string folder = SafePaths.ToDisplayPath(targetFolder ?? "");
        if (folder.Length > 0 && (!SafePaths.TryGetSafeRelative(folder, out string safeFolder, out _) || safeFolder != folder))
            return OperationResult.Fail("대상 폴더 이름이 올바르지 않습니다", folder);
        if (sources.Count == 0)
            return OperationResult.Fail("추가할 파일이 없습니다");
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var problems = new List<string>();
        string fullArchive;
        try
        {
            fullArchive = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축파일 경로가 올바르지 않습니다", ex.Message);
        }
        var items = SourceTree.Expand(sources, prefix, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullArchive }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "추가할 원본을 찾을 수 없습니다. 압축파일은 바뀌지 않았습니다", problems);
        if (SourceTree.DuplicateName(items) is { } dup)
            return OperationResult.Fail("추가할 원본 이름이 겹칩니다", dup);
        int fileCount = items.Count(i => !i.IsDirectory);

        return Rewrite(archivePath,
            validate: index =>
            {
                if (folder.Length > 0 && index.All.Contains(folder) && !index.Folders.Contains(folder))
                    return OperationResult.Fail("대상 경로가 폴더가 아니라 파일입니다", folder);
                var existing = new HashSet<string>(index.All, StringComparer.OrdinalIgnoreCase);
                var clash = items.Where(i => existing.Contains(i.EntryName)).Select(i => $"이미 있음: {i.EntryName}").ToArray();
                return clash.Length > 0 ? OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축파일은 바뀌지 않았습니다", clash) : null;
            },
            map: path => path,
            extra: index => folder.Length > 0 && !index.Folders.Contains(folder)
                ? [new SourceEntry(folder, null, true), .. items]
                : items,
            successMessage: $"파일 {fileCount}개를 추가했습니다",
            progress,
            cancel);
    }

    public OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        var targets = entryPaths.Select(SafePaths.ToDisplayPath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0)
            return OperationResult.Fail("지울 항목이 없습니다");
        return Rewrite(archivePath,
            validate: index =>
            {
                var missing = targets.Where(t => !index.All.Contains(t)).Select(t => $"없는 항목: {t}").ToArray();
                return missing.Length > 0 ? OperationResult.Fail("압축파일에 없는 항목입니다", missing) : null;
            },
            map: path => targets.Any(t => SafePaths.IsSelfOrDescendant(path, t)) ? null : path,
            extra: _ => [],
            successMessage: $"항목 {targets.Count}개를 지웠습니다",
            progress,
            cancel);
    }

    public OperationResult Rename(string archivePath, string entryPath, string newName, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        string old = SafePaths.ToDisplayPath(entryPath ?? "");
        if (old.Length == 0)
            return OperationResult.Fail("이름을 바꿀 항목이 없습니다");
        if (SafePaths.ValidateLeafName(newName) is { } nameError)
            return OperationResult.Fail("새 이름이 올바르지 않습니다", nameError);
        string parent = SafePaths.ParentOf(old);
        string renamed = parent.Length == 0 ? newName : parent + "/" + newName;
        if (renamed == old)
            return OperationResult.Ok("이름이 같아 바꾸지 않았습니다");

        return Rewrite(archivePath,
            validate: index =>
            {
                if (!index.All.Contains(old))
                    return OperationResult.Fail("압축파일에 없는 항목입니다", old);
                bool caseOnly = string.Equals(renamed, old, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && new HashSet<string>(index.All, StringComparer.OrdinalIgnoreCase).Contains(renamed))
                    return OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축파일은 바뀌지 않았습니다", renamed);
                return null;
            },
            map: path => SafePaths.IsSelfOrDescendant(path, old) ? renamed + path[old.Length..] : path,
            extra: _ => [],
            successMessage: $"'{old}' → '{renamed}'",
            progress,
            cancel);
    }

    public OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default)
        => OperationResult.Unsupported("이 형식은 압축파일 설명을 지원하지 않습니다");
}
