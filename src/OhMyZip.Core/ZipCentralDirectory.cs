using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OhMyZip.Core;

/// <summary>WinZip AES 확장 필드(0x9901). Version 1=AE-1(CRC 있음), 2=AE-2(CRC 0). Method 는 실제 압축 방식.</summary>
internal readonly record struct ZipAesInfo(ushort Version, byte Strength, ushort Method);

/// <summary>중앙 디렉터리 항목 하나의 원시 필드와 해석한 이름.</summary>
internal sealed record ZipRecord
{
    public required byte[] NameBytes { get; init; }
    public required string Name { get; init; }
    public required ushort VersionMadeBy { get; init; }
    public required ushort VersionNeeded { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Method { get; init; }
    public required uint DosTime { get; init; }
    public required uint Crc { get; init; }
    public required long CompressedSize { get; init; }
    public required long Size { get; init; }
    public required long Disk { get; init; }
    public required long LocalOffset { get; init; }
    public required ushort InternalAttributes { get; init; }
    public required uint ExternalAttributes { get; init; }
    public required byte[] Extra { get; init; }
    public required byte[] Comment { get; init; }
    public ZipAesInfo? Aes { get; init; }

    public bool IsEncrypted => (Flags & 0x0001) != 0;
    public bool HasDataDescriptor => (Flags & 0x0008) != 0;
    public bool IsDirectory => SafePaths.EndsWithSeparator(Name);

    /// <summary>외부 속성 상위 16비트의 유닉스 모드가 S_IFLNK(0xA000)이면 심볼릭 링크.</summary>
    public bool IsSymlink => ((ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>실제 압축 방식(AES 면 확장 필드 안의 값).</summary>
    public ushort ActualMethod => Aes?.Method ?? Method;

    /// <summary>AE-2 는 CRC 를 0 으로 두고 HMAC 으로만 검증한다.</summary>
    public uint? ExpectedCrc => IsDirectory || Aes is { Version: 2 } ? null : Crc;

    /// <summary>목록에 보이는 방식. 암호 항목은 암호 방식(AES-256 / ZipCrypto)을 보인다.</summary>
    public string DisplayMethod => Aes is { } aes
        ? $"AES-{ZipAes.KeyLength(aes.Strength) * 8}"
        : IsEncrypted ? "ZipCrypto" : ZipCentralDirectory.MethodName(Method);

    public DateTimeOffset? Modified
    {
        get
        {
            int time = (int)(DosTime & 0xFFFF), date = (int)(DosTime >> 16);
            int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
            int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
                return null;
            return new DateTimeOffset(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
        }
    }
}

/// <summary>중앙 디렉터리 전체와 압축파일 설명.</summary>
internal sealed record ZipDirectory(IReadOnlyList<ZipRecord> Entries, byte[] CommentBytes, string? Comment);

/// <summary>
/// ZIP 끝 기록(EOCD/ZIP64)과 중앙 디렉터리를 읽는다. 이름 해석: UTF-8 플래그(비트 11) → Info-ZIP 유니코드 경로(0x7075)
/// → ReadOptions.CodePage → 자동(플래그 없는 이름이 모두 올바른 UTF-8 이면 UTF-8, 아니면 한국어 Windows 기본 CP949).
/// 구조가 깨졌으면 InvalidDataException.
/// </summary>
internal static class ZipCentralDirectory
{
    public const uint LocalHeaderSignature = 0x04034b50;
    public const uint CentralHeaderSignature = 0x02014b50;
    public const uint EocdSignature = 0x06054b50;
    public const uint Zip64EocdSignature = 0x06064b50;
    public const uint Zip64LocatorSignature = 0x07064b50;
    public const uint DataDescriptorSignature = 0x08074b50;
    public const ushort Zip64ExtraId = 0x0001;
    public const ushort UnicodePathExtraId = 0x7075;
    public const int EocdLength = 22;

    static ZipCentralDirectory() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>코드페이지 번호 → 인코딩(949, 932, 936, 65001 …). 코드페이지 공급자를 먼저 등록한다.</summary>
    public static Encoding GetEncoding(int codePage) => Encoding.GetEncoding(codePage);

    /// <summary>뒤에서부터 EOCD 서명을 찾는다(설명 길이가 남은 바이트와 맞는 첫 후보).</summary>
    public static long FindEocd(Stream s)
    {
        long length = s.Length;
        if (length < EocdLength)
            throw new InvalidDataException("ZIP 끝 기록(EOCD)이 없습니다");
        int tailLength = (int)Math.Min(length, EocdLength + 0xFFFF);
        byte[] tail = new byte[tailLength];
        s.Position = length - tailLength;
        s.ReadExactly(tail);
        for (int i = tailLength - EocdLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSignature
                && i + EocdLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) <= tailLength)
                return length - tailLength + i;
        }
        throw new InvalidDataException("ZIP 끝 기록(EOCD)이 없습니다");
    }

    public static ZipDirectory Read(Stream s, ZipVolumeSet volumes, int? codePage)
    {
        long eocd = FindEocd(s);
        byte[] e = new byte[EocdLength];
        s.Position = eocd;
        s.ReadExactly(e);
        long cdDisk = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(6));
        long count = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(10));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(16));
        int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(20));
        byte[] comment = new byte[commentLength];
        s.ReadExactly(comment);
        long cdEnd = eocd;

        if (eocd >= 20)
        {
            byte[] loc = new byte[20];
            s.Position = eocd - 20;
            s.ReadExactly(loc);
            if (BinaryPrimitives.ReadUInt32LittleEndian(loc) == Zip64LocatorSignature)
            {
                long z64Disk = BinaryPrimitives.ReadUInt32LittleEndian(loc.AsSpan(4));
                long z64 = volumes.ToGlobal(z64Disk, (long)BinaryPrimitives.ReadUInt64LittleEndian(loc.AsSpan(8)));
                byte[] rec = new byte[56];
                s.Position = z64;
                s.ReadExactly(rec);
                if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != Zip64EocdSignature)
                    throw new InvalidDataException("ZIP64 끝 기록이 손상되었습니다");
                cdDisk = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(20));
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
                cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
                cdEnd = z64;
            }
        }

        long cdStart = volumes.ToGlobal(cdDisk, cdOffset);
        if (cdSize < 0 || cdSize > int.MaxValue || cdStart < 0 || cdStart + cdSize > cdEnd)
            throw new InvalidDataException("중앙 디렉터리 위치가 올바르지 않습니다");
        byte[] cd = new byte[cdSize];
        s.Position = cdStart;
        s.ReadExactly(cd);

        var raw = new List<(ZipRecord Record, string? Known)>();
        int pos = 0;
        for (long n = 0; n < count; n++)
        {
            if (pos + 46 > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos)) != CentralHeaderSignature)
                throw new InvalidDataException("중앙 디렉터리가 손상되었습니다");
            ReadOnlySpan<byte> h = cd.AsSpan(pos);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int entryCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            if (pos + 46 + nameLength + extraLength + entryCommentLength > cd.Length)
                throw new InvalidDataException("중앙 디렉터리가 손상되었습니다");
            byte[] nameBytes = h.Slice(46, nameLength).ToArray();
            byte[] extra = h.Slice(46 + nameLength, extraLength).ToArray();
            byte[] entryComment = h.Slice(46 + nameLength + extraLength, entryCommentLength).ToArray();
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);

            long size = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            long disk = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
            if (FindExtra(extra, Zip64ExtraId) is { } z64)
            {
                int p = 0;
                bool Take(out long value)
                {
                    value = 0;
                    if (p + 8 > z64.Length)
                        return false;
                    value = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(p));
                    p += 8;
                    return true;
                }
                if (size == 0xFFFFFFFF && !Take(out size))
                    throw new InvalidDataException("ZIP64 확장 필드가 손상되었습니다");
                if (compressed == 0xFFFFFFFF && !Take(out compressed))
                    throw new InvalidDataException("ZIP64 확장 필드가 손상되었습니다");
                if (offset == 0xFFFFFFFF && !Take(out offset))
                    throw new InvalidDataException("ZIP64 확장 필드가 손상되었습니다");
                if (disk == 0xFFFF && p + 4 <= z64.Length)
                    disk = BinaryPrimitives.ReadUInt32LittleEndian(z64.AsSpan(p));
            }
            if (size < 0 || compressed < 0 || offset < 0)
                throw new InvalidDataException("항목 크기가 올바르지 않습니다");

            ZipAesInfo? aes = null;
            if (FindExtra(extra, ZipAes.ExtraId) is { Length: >= 7 } a && a[2] == 'A' && a[3] == 'E')
                aes = new ZipAesInfo(BinaryPrimitives.ReadUInt16LittleEndian(a), a[4], BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(5)));

            string? known = null;
            if ((flags & 0x0800) != 0)
                known = Encoding.UTF8.GetString(nameBytes);
            else if (FindExtra(extra, UnicodePathExtraId) is { Length: > 5 } u && u[0] == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(1)) == Crc(nameBytes))
                known = Encoding.UTF8.GetString(u.AsSpan(5));

            raw.Add((new ZipRecord
            {
                NameBytes = nameBytes,
                Name = "",
                VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
                VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
                Flags = flags,
                Method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),
                DosTime = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),
                Crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),
                CompressedSize = compressed,
                Size = size,
                Disk = disk,
                LocalOffset = offset,
                InternalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(h[36..]),
                ExternalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(h[38..]),
                Extra = extra,
                Comment = entryComment,
                Aes = aes,
            }, known));
            pos += 46 + nameLength + extraLength + entryCommentLength;
        }

        Encoding legacy = codePage is { } cp ? GetEncoding(cp) : DetectLegacyEncoding(raw.Where(r => r.Known is null).Select(r => r.Record.NameBytes));
        var records = raw.Select(r => r.Record with { Name = r.Known ?? legacy.GetString(r.Record.NameBytes) }).ToList();
        return new ZipDirectory(records, comment, DecodeComment(comment, codePage));
    }

    /// <summary>자동 판별: 모두 올바른 UTF-8 이면 UTF-8, 하나라도 아니면 CP949.</summary>
    private static Encoding DetectLegacyEncoding(IEnumerable<byte[]> names)
    {
        foreach (byte[] name in names)
        {
            try
            {
                StrictUtf8.GetString(name);
            }
            catch (DecoderFallbackException)
            {
                return GetEncoding(949);
            }
        }
        return Encoding.UTF8;
    }

    /// <summary>압축파일 설명: 올바른 UTF-8 이면 UTF-8, 아니면 지정 코드페이지(없으면 CP949). 비어 있으면 null.</summary>
    private static string? DecodeComment(byte[] comment, int? codePage)
    {
        if (comment.Length == 0)
            return null;
        try
        {
            return StrictUtf8.GetString(comment);
        }
        catch (DecoderFallbackException)
        {
            return GetEncoding(codePage ?? 949).GetString(comment);
        }
    }

    /// <summary>확장 필드 묶음에서 id 의 데이터 부분을 찾는다. 없으면 null.</summary>
    public static byte[]? FindExtra(ReadOnlySpan<byte> extra, ushort id)
    {
        int p = 0;
        while (p + 4 <= extra.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            if (p + 4 + length > extra.Length)
                return null;
            if (tag == id)
                return extra.Slice(p + 4, length).ToArray();
            p += 4 + length;
        }
        return null;
    }

    /// <summary>확장 필드 묶음에서 ids 를 뺀 나머지(손상된 꼬리는 버린다).</summary>
    public static byte[] StripExtra(ReadOnlySpan<byte> extra, params ushort[] ids)
    {
        var kept = new List<byte>(extra.Length);
        int p = 0;
        while (p + 4 <= extra.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            if (p + 4 + length > extra.Length)
                break;
            if (!ids.Contains(tag))
                kept.AddRange(extra.Slice(p, 4 + length));
            p += 4 + length;
        }
        return [.. kept];
    }

    public static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    public static string MethodName(ushort method) => method switch
    {
        0 => "Store",
        8 => "Deflate",
        9 => "Deflate64",
        12 => "BZip2",
        14 => "LZMA",
        93 => "Zstd",
        95 => "XZ",
        98 => "PPMd",
        99 => "AES",
        _ => $"Method {method}",
    };
}

/// <summary>열린 ZIP 압축파일(한 파일 또는 분할 조각 묶음). 항목 데이터를 압축 해제·복호화해 연다. 한 번에 한 항목 스트림만 쓴다.</summary>
internal sealed class ZipArchiveReader : IDisposable
{
    private ZipArchiveReader(ZipVolumeSet volumes, Stream stream, ZipDirectory directory)
    {
        Volumes = volumes;
        Stream = stream;
        Directory = directory;
    }

    public ZipVolumeSet Volumes { get; }
    public Stream Stream { get; }
    public ZipDirectory Directory { get; }
    public IReadOnlyList<ZipRecord> Entries => Directory.Entries;
    public bool IsEncrypted => Entries.Any(e => e.IsEncrypted);

    public static ZipArchiveReader Open(string path, int? codePage)
    {
        var volumes = ZipVolumeSet.Resolve(path);
        var stream = volumes.OpenRead();
        try
        {
            return new ZipArchiveReader(volumes, stream, ZipCentralDirectory.Read(stream, volumes, codePage));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>지역 머리(고정 30바이트 + 이름 + 확장)를 읽어 돌려주고, 데이터 시작 전체 위치를 알려 준다.</summary>
    public byte[] ReadLocalHeader(ZipRecord r, out long dataStart)
    {
        long local = Volumes.ToGlobal(r.Disk, r.LocalOffset);
        byte[] fixedPart = new byte[30];
        Stream.Position = local;
        Stream.ReadExactly(fixedPart);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != ZipCentralDirectory.LocalHeaderSignature)
            throw new InvalidDataException($"{r.Name}: 지역 머리가 손상되었습니다");
        int n = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(26));
        int m = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(28));
        byte[] header = new byte[30 + n + m];
        fixedPart.CopyTo(header, 0);
        Stream.ReadExactly(header.AsSpan(30));
        dataStart = local + header.Length;
        if (dataStart + r.CompressedSize > Stream.Length)
            throw new InvalidDataException($"{r.Name}: 데이터가 압축파일 밖으로 나갑니다");
        return header;
    }

    /// <summary>
    /// 항목 내용을 연다. 암호 항목은 password 로 복호화한다: 없으면 ZipPasswordException(Missing), 검증값이 틀리면 ZipPasswordException.
    /// AES 인증 코드가 틀리면 끝까지 읽을 때 InvalidDataException. 지원하지 않는 방식은 NotSupportedException.
    /// </summary>
    public Stream OpenEntry(ZipRecord r, string? password)
    {
        ReadLocalHeader(r, out long dataStart);
        Stream data = new SubReadStream(Stream, dataStart, r.CompressedSize);
        WinZipAesReadStream? aes = null;
        if (r.IsEncrypted)
        {
            if ((r.Flags & 0x0040) != 0)
                throw new NotSupportedException("PKWARE 강력 암호화는 지원하지 않습니다");
            if (string.IsNullOrEmpty(password))
                throw new ZipPasswordException(missing: true);
            if (r.Method == ZipAes.Method)
            {
                if (r.Aes is not { } info)
                    throw new InvalidDataException($"{r.Name}: AES 확장 필드가 없습니다");
                data = aes = WinZipAesReadStream.Open(data, r.CompressedSize, info.Strength, password);
            }
            else
            {
                byte check = r.HasDataDescriptor ? (byte)((r.DosTime >> 8) & 0xFF) : (byte)(r.Crc >> 24);
                data = ZipCryptoReadStream.Open(data, r.CompressedSize, password, check);
            }
        }
        Stream content = r.ActualMethod switch
        {
            0 => data,
            8 => new DeflateStream(data, CompressionMode.Decompress),
            9 => new SharpCompress.Compressors.Deflate64.Deflate64Stream(data, SharpCompress.Compressors.CompressionMode.Decompress),
            _ => throw new NotSupportedException($"지원하지 않는 압축 방식: {ZipCentralDirectory.MethodName(r.ActualMethod)}"),
        };
        return aes is null || content == data ? content : new DrainOnEndStream(content, aes);
    }

    /// <summary>암호가 맞는지 첫 암호 파일 항목으로 확인한다(맞지 않으면 ZipPasswordException).</summary>
    public void CheckPassword(string? password)
    {
        var first = Entries.FirstOrDefault(e => e.IsEncrypted && !e.IsDirectory);
        if (first is not null)
            OpenEntry(first, password).Dispose();
    }

    public void Dispose() => Stream.Dispose();

    /// <summary>압축 해제기가 끝을 알리면 AES 암호문 나머지를 소비해 인증 코드까지 확인한다.</summary>
    private sealed class DrainOnEndStream(Stream content, WinZipAesReadStream aes) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = content.Read(buffer);
            if (n == 0 && buffer.Length > 0)
                aes.Drain();
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
                aes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
