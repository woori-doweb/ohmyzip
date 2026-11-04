using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>
/// ZIP 쓰기. 새 항목(저장/Deflate, 선택적으로 WinZip AES-256 AE-2)과 기존 항목의 원시 복사(복호화·재압축 없음)를 같은 출력에 쓴다.
/// 이름은 언제나 UTF-8 + 플래그 비트 11. 지역 머리의 CRC·크기는 데이터를 쓴 뒤 그 자리를 고친다(데이터 설명자 없음).
/// 4 GiB 를 넘을 수 있는 항목·위치·개수는 ZIP64 로 쓴다.
/// </summary>
internal sealed class ZipWriter(ZipVolumeWriter output)
{
    private const int BufferSize = 81920;
    private const ushort VersionMadeBy = 63; // 호스트 0(MS-DOS/FAT), 규격 6.3
    private const ushort Utf8Flag = 0x0800;
    /// <summary>원본 파일이 이보다 크면 지역 머리에 ZIP64 자리를 미리 둔다(Deflate 팽창·AES 덧붙임 여유 1 MiB).</summary>
    private const long Zip64Threshold = 0xFFF00000;

    private sealed record Central(ushort MadeBy, ushort VersionNeeded, ushort Flags, ushort Method, uint DosTime, uint Crc, long CompressedSize, long Size,
        int Disk, long Offset, ushort InternalAttributes, uint ExternalAttributes, byte[] NameBytes, byte[] Extra, byte[] Comment);

    private readonly List<Central> _central = [];

    /// <summary>압축 수준 0 = 저장(방식 0), 1~3 = 가장 빠른 Deflate, 4~6 = 최적, 7~9 = 가장 작게.</summary>
    public static (ushort Method, CompressionLevel Level) MapLevel(int level) => level switch
    {
        0 => (0, CompressionLevel.NoCompression),
        <= 3 => (8, CompressionLevel.Fastest),
        <= 6 => (8, CompressionLevel.Optimal),
        _ => (8, CompressionLevel.SmallestSize),
    };

    public static uint ToDosTime(DateTime local)
    {
        if (local.Year < 1980)
            local = new DateTime(1980, 1, 1, 0, 0, 0);
        else if (local.Year > 2107)
            local = new DateTime(2107, 12, 31, 23, 59, 58);
        uint time = (uint)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
        uint date = (uint)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
        return time | (date << 16);
    }

    public void AddDirectory(string entryName, DateTime modified)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName.EndsWith('/') ? entryName : entryName + "/");
        var (disk, offset) = output.NextPosition();
        uint dos = ToDosTime(modified);
        output.Write(LocalHeader(20, Utf8Flag, 0, dos, 0, 0, 0, name, []));
        _central.Add(new Central(VersionMadeBy, 20, Utf8Flag, 0, dos, 0, 0, 0, disk, offset, 0, 0x10, name, [], []));
    }

    /// <summary>원본 스트림을 압축(·암호화)해 쓴다. password 가 있으면 AES-256(AE-2). onRead 는 원본에서 읽은 바이트 수를 알린다.</summary>
    public void AddFile(string entryName, Stream source, DateTime modified, int level, string? password, Action<int>? onRead, CancellationToken cancel)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        var (method, compression) = MapLevel(level);
        bool aes = !string.IsNullOrEmpty(password);
        bool zip64 = source.Length >= Zip64Threshold;
        byte[] aesExtra = aes ? ZipAes.ExtraField(method) : [];
        byte[] localExtra = zip64 ? [.. aesExtra, .. Zip64Extra(0, 0, null, null, forceBoth: true)] : aesExtra;
        ushort flags = (ushort)(Utf8Flag | (aes ? 0x0001 : 0));
        ushort storedMethod = aes ? ZipAes.Method : method;
        ushort versionNeeded = aes ? (ushort)51 : zip64 ? (ushort)45 : (ushort)20;
        uint dos = ToDosTime(modified);
        uint placeholder = zip64 ? 0xFFFFFFFF : 0;

        var (disk, offset) = output.NextPosition();
        output.Write(LocalHeader(versionNeeded, flags, storedMethod, dos, 0, placeholder, placeholder, name, localExtra));
        long dataStart = output.TotalWritten;

        var crc = new Crc32();
        long size = 0;
        WinZipAesWriteStream? encryptor = aes ? new WinZipAesWriteStream(output, password!) : null;
        try
        {
            Stream sink = encryptor ?? (Stream)output;
            Stream? deflate = method == 8 ? new DeflateStream(sink, compression, leaveOpen: true) : null;
            try
            {
                Stream target = deflate ?? sink;
                byte[] buffer = new byte[BufferSize];
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    crc.Append(buffer.AsSpan(0, n));
                    target.Write(buffer, 0, n);
                    size += n;
                    onRead?.Invoke(n);
                }
            }
            finally
            {
                deflate?.Dispose();
            }
            encryptor?.Finish();
        }
        finally
        {
            encryptor?.Dispose();
        }
        long compressed = output.TotalWritten - dataStart;
        if (!zip64 && (size >= 0xFFFFFFFF || compressed >= 0xFFFFFFFF))
            throw new IOException(string.Format(EngineText.Zip_FileGrewPast4GiB, entryName));
        uint storedCrc = aes ? 0 : crc.Value; // AE-2: CRC 0, 무결성은 HMAC

        byte[] patch = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(patch, storedCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(4), zip64 ? 0xFFFFFFFF : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(8), zip64 ? 0xFFFFFFFF : (uint)size);
        output.Patch(disk, offset + 14, patch);
        if (zip64)
        {
            byte[] sizes = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(sizes, (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(8), (ulong)compressed);
            output.Patch(disk, offset + 30 + name.Length + aesExtra.Length + 4, sizes);
        }
        _central.Add(new Central(VersionMadeBy, versionNeeded, flags, storedMethod, dos, storedCrc, compressed, size, disk, offset, 0, 0x20, name, aesExtra, []));
    }

    /// <summary>
    /// 기존 항목을 그대로 옮긴다(암호 항목도 복호화하지 않음). newName 이 있으면 UTF-8 이름으로 바꾸고 플래그 비트 11 을 켠다
    /// (낡은 0x7075 유니코드 경로는 뺀다). 데이터 설명자(비트 3)가 있던 항목은 중앙 디렉터리 값으로 설명자를 다시 쓴다.
    /// </summary>
    public void CopyRaw(ZipArchiveReader reader, ZipRecord r, string? newName, Action<int>? onCopied, CancellationToken cancel)
    {
        byte[] local = reader.ReadLocalHeader(r, out long dataStart);
        int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26));
        byte[] localExtra = local[(30 + localNameLength)..];
        byte[] name = local[30..(30 + localNameLength)];
        byte[] centralName = r.NameBytes;
        ushort localFlags = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(6));
        ushort centralFlags = r.Flags;
        byte[] centralExtra = ZipCentralDirectory.StripExtra(r.Extra, ZipCentralDirectory.Zip64ExtraId);
        if (newName is not null)
        {
            name = centralName = Encoding.UTF8.GetBytes(newName);
            localFlags |= Utf8Flag;
            centralFlags |= Utf8Flag;
            localExtra = ZipCentralDirectory.StripExtra(localExtra, ZipCentralDirectory.UnicodePathExtraId);
            centralExtra = ZipCentralDirectory.StripExtra(centralExtra, ZipCentralDirectory.UnicodePathExtraId);
        }
        byte[] header = new byte[30 + name.Length + localExtra.Length];
        local.AsSpan(0, 30).CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), localFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)localExtra.Length);
        name.CopyTo(header, 30);
        localExtra.CopyTo(header, 30 + name.Length);

        cancel.ThrowIfCancellationRequested();
        var (disk, offset) = output.NextPosition();
        output.Write(header);
        byte[] buffer = new byte[BufferSize];
        long left = r.CompressedSize;
        reader.Stream.Position = dataStart;
        while (left > 0)
        {
            cancel.ThrowIfCancellationRequested();
            int n = reader.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0)
                throw new InvalidDataException(string.Format(EngineText.Zip_DataTruncated, r.Name));
            output.Write(buffer, 0, n);
            left -= n;
            onCopied?.Invoke(n);
        }
        if (r.HasDataDescriptor)
        {
            bool wide = ZipCentralDirectory.FindExtra(localExtra, ZipCentralDirectory.Zip64ExtraId) is not null;
            byte[] descriptor = new byte[wide ? 24 : 16];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, ZipCentralDirectory.DataDescriptorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), r.Crc);
            if (wide)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)r.CompressedSize);
                BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(16), (ulong)r.Size);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)r.CompressedSize);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), (uint)r.Size);
            }
            output.Write(descriptor);
        }
        _central.Add(new Central(r.VersionMadeBy, r.VersionNeeded, centralFlags, r.Method, r.DosTime, r.Crc, r.CompressedSize, r.Size, disk, offset,
            r.InternalAttributes, r.ExternalAttributes, centralName, centralExtra, r.Comment));
    }

    /// <summary>중앙 디렉터리와 끝 기록(필요하면 ZIP64)을 쓴다. 분할로 시작했는데 한 조각에 다 들어가면 보통 ZIP 한 파일로 만든다.</summary>
    public void Finish(byte[] comment)
    {
        if (comment.Length > 0xFFFF)
            throw new IOException(EngineText.Zip_ArchiveCommentTooLong);
        long tail = _central.Sum(c => (long)CentralHeader(c).Length) + 56 + 20 + ZipCentralDirectory.EocdLength + comment.Length;
        if (output.TryCollapseToSingle(tail))
        {
            for (int i = 0; i < _central.Count; i++)
                _central[i] = _central[i] with { Offset = _central[i].Offset - 4 };
        }

        var (cdDisk, cdOffset) = output.NextPosition();
        long cdStartTotal = output.TotalWritten;
        var recordDisks = new List<int>(_central.Count);
        foreach (var c in _central)
        {
            recordDisks.Add(output.NextPosition().Disk);
            output.Write(CentralHeader(c));
        }
        long cdSize = output.TotalWritten - cdStartTotal;
        long count = _central.Count;
        bool zip64 = count >= 0xFFFF || cdSize >= 0xFFFFFFFF || cdOffset >= 0xFFFFFFFF || cdDisk >= 0xFFFF;
        var (lastDisk, z64Offset) = output.EnsureRoom((zip64 ? 56 + 20 : 0) + ZipCentralDirectory.EocdLength + comment.Length);
        if (lastDisk >= 0xFFFF)
            zip64 = true;
        long onThisDisk = recordDisks.Count(d => d == lastDisk);

        if (zip64)
        {
            byte[] z = new byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(z, ZipCentralDirectory.Zip64EocdSignature);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(4), 44);
            BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(12), VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(14), 45);
            BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(16), (uint)lastDisk);
            BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(20), (uint)cdDisk);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(24), (ulong)onThisDisk);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(32), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(40), (ulong)cdSize);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(48), (ulong)cdOffset);
            output.Write(z);
            byte[] loc = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(loc, ZipCentralDirectory.Zip64LocatorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(loc.AsSpan(4), (uint)lastDisk);
            BinaryPrimitives.WriteUInt64LittleEndian(loc.AsSpan(8), (ulong)z64Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(loc.AsSpan(16), (uint)lastDisk + 1);
            output.Write(loc);
        }
        byte[] e = new byte[ZipCentralDirectory.EocdLength + comment.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(e, ZipCentralDirectory.EocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), (ushort)Math.Min(lastDisk, 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), (ushort)Math.Min(cdDisk, 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), (ushort)Math.Min(onThisDisk, 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), (ushort)Math.Min(count, 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)Math.Min(cdSize, 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(16), (uint)Math.Min(cdOffset, 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)comment.Length);
        comment.CopyTo(e, ZipCentralDirectory.EocdLength);
        output.Write(e);
        output.Flush();
    }

    private static byte[] LocalHeader(ushort versionNeeded, ushort flags, ushort method, uint dosTime, uint crc, uint compressed, uint size, byte[] name, byte[] extra)
    {
        byte[] h = new byte[30 + name.Length + extra.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(h, ZipCentralDirectory.LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), versionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), method);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(10), dosTime);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(18), compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(22), size);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)extra.Length);
        name.CopyTo(h, 30);
        extra.CopyTo(h, 30 + name.Length);
        return h;
    }

    private static byte[] CentralHeader(Central c)
    {
        bool bigSize = c.Size >= 0xFFFFFFFF, bigCompressed = c.CompressedSize >= 0xFFFFFFFF, bigOffset = c.Offset >= 0xFFFFFFFF, bigDisk = c.Disk >= 0xFFFF;
        byte[] z64 = bigSize || bigCompressed || bigOffset || bigDisk
            ? Zip64Extra(bigSize ? c.Size : null, bigCompressed ? c.CompressedSize : null, bigOffset ? c.Offset : null, bigDisk ? c.Disk : null, forceBoth: false)
            : [];
        byte[] extra = [.. z64, .. c.Extra];
        ushort versionNeeded = z64.Length > 0 ? Math.Max(c.VersionNeeded, (ushort)45) : c.VersionNeeded;
        byte[] h = new byte[46 + c.NameBytes.Length + extra.Length + c.Comment.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(h, ZipCentralDirectory.CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), c.MadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), versionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), c.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), c.Method);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), c.DosTime);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), c.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), bigCompressed ? 0xFFFFFFFF : (uint)c.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), bigSize ? 0xFFFFFFFF : (uint)c.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)c.NameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(30), (ushort)extra.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)c.Comment.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), bigDisk ? (ushort)0xFFFF : (ushort)c.Disk);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(36), c.InternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(38), c.ExternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), bigOffset ? 0xFFFFFFFF : (uint)c.Offset);
        c.NameBytes.CopyTo(h, 46);
        extra.CopyTo(h, 46 + c.NameBytes.Length);
        c.Comment.CopyTo(h, 46 + c.NameBytes.Length + extra.Length);
        return h;
    }

    /// <summary>ZIP64 확장 필드(0x0001). 순서: 원래 크기, 압축 크기, 지역 머리 위치, 디스크 번호. 지역 머리는 두 크기를 모두 둔다(forceBoth).</summary>
    private static byte[] Zip64Extra(long? size, long? compressed, long? offset, long? disk, bool forceBoth)
    {
        var data = new List<byte>();
        void Add64(long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v);
            data.AddRange(b.ToArray());
        }
        if (forceBoth || size is not null)
            Add64(size ?? 0);
        if (forceBoth || compressed is not null)
            Add64(compressed ?? 0);
        if (offset is { } o)
            Add64(o);
        if (disk is { } d)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)d);
            data.AddRange(b.ToArray());
        }
        byte[] extra = new byte[4 + data.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, ZipCentralDirectory.Zip64ExtraId);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), (ushort)data.Count);
        data.CopyTo(extra, 4);
        return extra;
    }
}
