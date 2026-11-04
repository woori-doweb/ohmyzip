using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>분할 압축 조각 이름 규칙: name.7z → name.7z.001, name.7z.002 …</summary>
internal static class Volumes
{
    /// <summary>분할 조각의 최소 크기(64 KiB). 더 작으면 조각 수가 지나치게 많아진다.</summary>
    public const long MinVolumeSize = 64 * 1024;

    public static string PartPath(string basePath, int index) => $"{basePath}.{index:D3}";

    /// <summary>"name.7z.001" 처럼 첫 조각 이름인지.</summary>
    public static bool IsFirstPart(string path) => path.EndsWith(".001", StringComparison.Ordinal);

    /// <summary>첫 조각에서 시작해 이어지는 조각을 빈 번호가 나올 때까지 모은다.</summary>
    public static List<string> ConsecutiveParts(string firstPart)
    {
        string basePath = firstPart[..^4];
        var parts = new List<string>();
        for (int i = 1; i <= 999; i++)
        {
            string p = PartPath(basePath, i);
            if (!File.Exists(p))
                break;
            parts.Add(p);
        }
        return parts;
    }
}

/// <summary>
/// 쓰기용 분할 스트림. 조각마다 정확히 volumeSize 바이트를 쓰고(마지막 조각만 작을 수 있음) 다음 조각으로 넘어간다.
/// 7Z 시작 헤더를 마지막에 고쳐 쓰도록 첫 조각 안으로는 되돌아가 쓸 수 있다(첫 조각과 현재 조각만 열어 둔다).
/// </summary>
internal sealed class VolumeWriteStream : Stream
{
    private readonly Func<int, string> _partPath;
    private readonly long _volumeSize;
    private readonly List<string> _parts = [];
    private FileStream? _first;
    private FileStream? _current;
    private int _currentIndex = -1;
    private long _position;
    private long _length;

    public VolumeWriteStream(Func<int, string> partPath, long volumeSize)
    {
        if (volumeSize < Volumes.MinVolumeSize)
            throw new ArgumentOutOfRangeException(nameof(volumeSize));
        _partPath = partPath;
        _volumeSize = volumeSize;
    }

    /// <summary>지금까지 만든 조각 파일 경로(1번부터 차례로).</summary>
    public IReadOnlyList<string> Parts => _parts;

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    private FileStream PartStream(int index)
    {
        if (index == 0 && _first is not null)
            return _first;
        if (index == _currentIndex && _current is not null)
            return _current;
        if (index < _parts.Count && index != 0)
            throw new NotSupportedException(EngineText.Volume_CannotSeekBackToWrite);
        // 새 조각은 차례대로만 만든다.
        while (_parts.Count <= index)
        {
            string path = _partPath(_parts.Count + 1);
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _parts.Add(path);
            if (_parts.Count == 1)
            {
                _first = fs;
                continue;
            }
            if (_current is not null && _current != _first)
                _current.Dispose();
            _current = fs;
            _currentIndex = _parts.Count - 1;
        }
        return index == 0 ? _first! : _current!;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int index = (int)(_position / _volumeSize);
            long inPart = _position % _volumeSize;
            int n = (int)Math.Min(buffer.Length, _volumeSize - inPart);
            var fs = PartStream(index);
            fs.Position = inPart;
            fs.Write(buffer[..n]);
            buffer = buffer[n..];
            _position += n;
            _length = Math.Max(_length, _position);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0 || target > _length)
            throw new IOException(EngineText.Volume_SeekOutOfRange);
        _position = target;
        return target;
    }

    public override void Flush()
    {
        _first?.Flush();
        _current?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_current is not null && _current != _first)
                _current.Dispose();
            _first?.Dispose();
            _current = null;
            _first = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>여러 조각 파일을 하나의 읽기 전용·탐색 가능 스트림으로 잇는다.</summary>
internal sealed class VolumeReadStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;

    public VolumeReadStream(IReadOnlyList<string> paths)
    {
        _parts = new FileStream[paths.Count];
        _starts = new long[paths.Count];
        try
        {
            long start = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                _parts[i] = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                _starts[i] = start;
                start += _parts[i].Length;
            }
            _length = start;
        }
        catch
        {
            foreach (var p in _parts)
                p?.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= _length)
            return 0;
        int index = Array.BinarySearch(_starts, _position);
        if (index < 0)
            index = ~index - 1;
        // 크기 0 인 조각은 건너뛴다.
        while (index < _parts.Length - 1 && _position >= _starts[index] + _parts[index].Length)
            index++;
        var part = _parts[index];
        part.Position = _position - _starts[index];
        int want = (int)Math.Min(buffer.Length, part.Length - part.Position);
        int n = part.Read(buffer[..want]);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0)
            throw new IOException(EngineText.Stream_SeekBeforeStart);
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var p in _parts)
                p.Dispose();
        }
        base.Dispose(disposing);
    }
}
