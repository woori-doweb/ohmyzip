using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace OhMyZip.Core;

/// <summary>분할 ZIP 의 조각 파일이 없다. 메시지에 없는 파일 이름이 들어간다.</summary>
internal sealed class ZipVolumeMissingException(string fileName)
    : IOException($"분할 압축 파일의 조각이 없습니다: {Path.GetFileName(fileName)}")
{
    public string FileName { get; } = fileName;
}

/// <summary>
/// ZIP 압축 파일을 이루는 파일들. 보통은 한 파일, 분할 ZIP(APPNOTE 8.5)은 name.z01, name.z02, …, name.zip(마지막).
/// 중앙 디렉터리의 (디스크 번호, 디스크 안 위치)를 조각을 이어 붙인 전체 위치로 바꿔 준다.
/// </summary>
internal sealed partial class ZipVolumeSet
{
    private readonly long[] _starts;

    private ZipVolumeSet(IReadOnlyList<string> parts)
    {
        Parts = parts;
        _starts = new long[parts.Count];
        long start = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            _starts[i] = start;
            start += new FileInfo(parts[i]).Length;
        }
    }

    public IReadOnlyList<string> Parts { get; }

    public bool IsSplit => Parts.Count > 1;

    [GeneratedRegex(@"^\.z\d{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPartExtension();

    /// <summary>name.zNN 을 열면 같은 이름의 name.zip(마지막 조각)에서 시작한다. 마지막 조각 EOCD 의 디스크 번호로 앞 조각들을 찾는다.</summary>
    public static ZipVolumeSet Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        string last = full;
        if (SplitPartExtension().IsMatch(Path.GetExtension(full)))
        {
            last = Path.ChangeExtension(full, ".zip");
            if (!File.Exists(last))
                throw new ZipVolumeMissingException(last);
        }
        if (!File.Exists(last))
            throw new FileNotFoundException("압축 파일이 없습니다", last);
        int disk;
        using (var fs = new FileStream(last, FileMode.Open, FileAccess.Read, FileShare.Read))
            disk = LastDiskNumber(fs);
        if (disk == 0)
            return new ZipVolumeSet([last]);
        var parts = new List<string>();
        for (int i = 1; i <= disk; i++)
        {
            string part = PartPath(last, i);
            if (!File.Exists(part))
                throw new ZipVolumeMissingException(part);
            parts.Add(part);
        }
        parts.Add(last);
        return new ZipVolumeSet(parts);
    }

    /// <summary>분할 조각 i(1부터) 경로: name.z01 …</summary>
    public static string PartPath(string lastPath, int index) => Path.ChangeExtension(lastPath, $".z{index:D2}");

    /// <summary>마지막 조각의 EOCD(또는 ZIP64 위치 기록)가 말하는 "이 디스크 번호". EOCD 가 없으면 InvalidDataException.</summary>
    private static int LastDiskNumber(FileStream fs)
    {
        long eocd = ZipCentralDirectory.FindEocd(fs);
        byte[] record = new byte[22];
        fs.Position = eocd;
        fs.ReadExactly(record);
        int disk = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        if (disk == 0xFFFF && eocd >= 20)
        {
            byte[] locator = new byte[20];
            fs.Position = eocd - 20;
            fs.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == ZipCentralDirectory.Zip64LocatorSignature)
                disk = (int)BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) - 1;
        }
        return disk;
    }

    /// <summary>(디스크, 디스크 안 위치) → 이어 붙인 전체 위치. 한 파일이면 디스크 번호는 보지 않는다.</summary>
    public long ToGlobal(long disk, long offset)
    {
        if (!IsSplit)
            return offset;
        if (disk < 0 || disk >= _starts.Length)
            throw new InvalidDataException($"없는 디스크 번호 {disk}");
        return _starts[disk] + offset;
    }

    public Stream OpenRead() => new ConcatReadStream(Parts);
}

/// <summary>여러 파일을 이어 붙인 읽기 전용·탐색 가능 스트림.</summary>
internal sealed class ConcatReadStream : Stream
{
    private readonly FileStream[] _files;
    private readonly long[] _starts;
    private long _position;

    public ConcatReadStream(IReadOnlyList<string> paths)
    {
        _files = new FileStream[paths.Count];
        _starts = new long[paths.Count];
        try
        {
            long start = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                _files[i] = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                _starts[i] = start;
                start += _files[i].Length;
            }
            Length = start;
        }
        catch
        {
            Dispose(true);
            throw;
        }
    }

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            int i = Array.BinarySearch(_starts, _position);
            if (i < 0)
                i = ~i - 1;
            while (i + 1 < _starts.Length && _starts[i + 1] <= _position)
                i++;
            var file = _files[i];
            file.Position = _position - _starts[i];
            int want = (int)Math.Min(buffer.Length, file.Length - file.Position);
            int n = file.Read(buffer[..want]);
            if (n <= 0)
                break;
            _position += n;
            total += n;
            buffer = buffer[n..];
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in _files)
                f?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush()
    {
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>바탕 스트림의 [start, start+length) 구간만 읽는 창. 읽을 때마다 바탕 위치를 맞추므로 한 번에 하나만 쓴다. 바탕 스트림은 닫지 않는다.</summary>
internal sealed class SubReadStream(Stream inner, long start, long length) : Stream
{
    private long _offset;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long left = length - _offset;
        if (left <= 0 || buffer.Length == 0)
            return 0;
        inner.Position = start + _offset;
        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        _offset += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _offset; set => throw new NotSupportedException(); }
    public override void Flush()
    {
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// ZIP 쓰기 출력. 같은 폴더 임시 파일(들)에 쓰고 <see cref="Commit"/> 에서만 제 이름으로 옮긴다. 닫힐 때까지 Commit 하지 않으면 임시 파일을 지운다.
/// 분할이면 첫 조각 앞에 분할 표식(0x08074b50)을 쓰고 정확히 volumeSize 바이트마다 다음 조각으로 넘어간다
/// (데이터·머리는 조각 경계를 넘을 수 있고, EOCD 묶음만 마지막 조각 안에 둔다). 결과는 name.z01…name.zip.
/// </summary>
internal sealed class ZipVolumeWriter : Stream
{
    public const long MinVolumeSize = 64 * 1024;
    public const uint SpanMarker = 0x08074b50;

    private readonly string _finalPath;
    private long _volumeSize;
    private readonly List<string> _temps = [];
    private readonly List<long> _lengths = [];
    private FileStream _current = null!;
    private long _total;
    private bool _committed;

    private ZipVolumeWriter(string finalPath, long? volumeSize)
    {
        _finalPath = finalPath;
        _volumeSize = volumeSize ?? long.MaxValue;
        OpenVolume();
        if (IsSplit)
        {
            Span<byte> marker = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(marker, SpanMarker);
            Write(marker);
        }
    }

    /// <summary>volumeSize=null 이면 한 파일. 분할 크기는 <see cref="MinVolumeSize"/> 이상이어야 한다(호출자가 확인).</summary>
    public static ZipVolumeWriter Create(string finalPath, long? volumeSize) => new(finalPath, volumeSize);

    public bool IsSplit => _volumeSize != long.MaxValue;

    public int Disk => _temps.Count - 1;

    public long DiskOffset => _current.Position;

    /// <summary>지금까지 쓴 전체 바이트(분할 표식 포함).</summary>
    public long TotalWritten => _total;

    private void OpenVolume()
    {
        if (_temps.Count > 0)
        {
            _lengths.Add(_current.Length);
            _current.Dispose();
        }
        string temp = AtomicFile.TempSibling(_finalPath);
        _temps.Add(temp);
        _current = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            long room = _volumeSize - _current.Position;
            if (room == 0)
            {
                OpenVolume();
                continue;
            }
            int n = (int)Math.Min(buffer.Length, room);
            _current.Write(buffer[..n]);
            _total += n;
            buffer = buffer[n..];
        }
    }

    /// <summary>다음에 쓸 바이트의 (디스크, 위치). 지금 조각이 꽉 찼으면 다음 조각을 연다.</summary>
    public (int Disk, long Offset) NextPosition() => EnsureRoom(1);

    /// <summary>length 바이트를 조각 경계 없이 쓸 수 있게 한다(EOCD 묶음용). 남은 자리가 모자라면 다음 조각을 연다.</summary>
    public (int Disk, long Offset) EnsureRoom(long length)
    {
        if (length > _volumeSize)
            throw new IOException("분할 크기가 압축 파일 끝 기록보다 작습니다");
        if (_volumeSize - _current.Position < length)
            OpenVolume();
        return (Disk, DiskOffset);
    }

    /// <summary>이미 쓴 자리(지역 머리의 CRC·크기)를 고친다. 조각 경계를 넘으면 이어서 다음 조각에 쓴다.</summary>
    public void Patch(int disk, long offset, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            long length = disk == Disk ? _current.Length : _lengths[disk];
            if (offset >= length)
            {
                offset -= length;
                disk++;
                continue;
            }
            int n = (int)Math.Min(data.Length, length - offset);
            if (disk == Disk)
            {
                long end = _current.Position;
                _current.Position = offset;
                _current.Write(data[..n]);
                _current.Position = end;
            }
            else
            {
                using var fs = new FileStream(_temps[disk], FileMode.Open, FileAccess.Write, FileShare.None);
                fs.Position = offset;
                fs.Write(data[..n]);
            }
            data = data[n..];
            disk++;
            offset = 0;
        }
    }

    /// <summary>
    /// 분할로 시작했지만 남은 끝 기록(tailLength)까지 첫 조각에 다 들어가면 분할 표식을 빼고 보통 ZIP 한 파일로 바꾼다.
    /// 바뀌면 true — 이미 쓴 모든 위치가 4바이트 앞당겨진다.
    /// </summary>
    public bool TryCollapseToSingle(long tailLength)
    {
        if (!IsSplit || Disk != 0 || _current.Position + tailLength > _volumeSize)
            return false;
        string temp = AtomicFile.TempSibling(_finalPath);
        var target = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920);
        try
        {
            _current.Position = 4;
            _current.CopyTo(target);
        }
        catch
        {
            target.Dispose();
            AtomicFile.TryDelete(temp);
            throw;
        }
        _current.Dispose();
        AtomicFile.TryDelete(_temps[0]);
        _temps[0] = temp;
        _current = target;
        _total -= 4;
        _volumeSize = long.MaxValue;
        return true;
    }

    /// <summary>임시 파일을 제 이름으로 옮긴다. 분할이면 name.z01… 과 마지막 name.zip. 하나라도 실패하면 이미 옮긴 것을 지우고 예외.</summary>
    public IReadOnlyList<string> Commit(bool overwrite)
    {
        _current.Flush();
        _lengths.Add(_current.Length);
        _current.Dispose();
        var targets = new List<string>();
        for (int i = 0; i < _temps.Count; i++)
            targets.Add(i == _temps.Count - 1 ? _finalPath : ZipVolumeSet.PartPath(_finalPath, i + 1));
        if (!overwrite && targets.FirstOrDefault(File.Exists) is { } existing)
            throw new IOException($"같은 이름의 파일이 이미 있습니다: {Path.GetFileName(existing)}");
        var moved = new List<string>();
        try
        {
            for (int i = 0; i < _temps.Count; i++)
            {
                AtomicFile.Commit(_temps[i], targets[i], overwrite);
                moved.Add(targets[i]);
            }
        }
        catch
        {
            foreach (string m in moved)
                AtomicFile.TryDelete(m);
            throw;
        }
        _committed = true;
        return targets;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current.Dispose();
            if (!_committed)
            {
                foreach (string t in _temps)
                    AtomicFile.TryDelete(t);
            }
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _current.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
