using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>
/// 7-Zip 의 7zAES 코더(ID 06 F1 07 01) 암호화 쪽. 읽기(복호화)는 SharpCompress 가 맡는다.
/// 키 = SHA-256( (salt ‖ UTF-16LE 암호 ‖ 8바이트 LE 카운터) 를 2^NumCyclesPower 번 이어 붙인 것 ),
/// 암호화 = AES-256-CBC, 무작위 16바이트 IV, 마지막 블록은 0 으로 채운다(7-Zip 과 같음).
/// </summary>
internal static class SevenZipAes
{
    public static readonly byte[] CoderId = [0x06, 0xF1, 0x07, 0x01];

    /// <summary>키 유도 반복 지수(2^19 회, 7-Zip 기본값).</summary>
    public const int NumCyclesPower = 19;

    public const int SaltSize = 16;
    public const int IvSize = 16;

    public static byte[] DeriveKey(string password, byte[] salt, int numCyclesPower = NumCyclesPower)
    {
        byte[] pw = Encoding.Unicode.GetBytes(password);
        byte[] block = new byte[salt.Length + pw.Length + 8];
        salt.CopyTo(block, 0);
        pw.CopyTo(block, salt.Length);
        int counterAt = salt.Length + pw.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rounds = 1L << numCyclesPower;
        for (long round = 0; round < rounds; round++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(counterAt), round);
            sha.AppendData(block);
        }
        CryptographicOperations.ZeroMemory(pw);
        CryptographicOperations.ZeroMemory(block);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// 코더 속성: [cycles | 0x80(salt 있음) | 0x40(IV 있음)], [(saltSize-1)&lt;&lt;4 | (ivSize-1)], salt, IV.
    /// 7-Zip 은 saltSize = bit7 + 상위 니블, ivSize = bit6 + 하위 니블 로 읽는다.
    /// </summary>
    public static byte[] Properties(byte[] salt, byte[] iv)
    {
        var props = new byte[2 + salt.Length + iv.Length];
        props[0] = (byte)(NumCyclesPower | (salt.Length > 0 ? 0x80 : 0) | (iv.Length > 0 ? 0x40 : 0));
        props[1] = (byte)(((salt.Length > 0 ? salt.Length - 1 : 0) << 4) | (iv.Length > 0 ? iv.Length - 1 : 0));
        salt.CopyTo(props, 2);
        iv.CopyTo(props, 2 + salt.Length);
        return props;
    }
}

/// <summary>
/// 쓰기 전용 AES-256-CBC 암호화 스트림. 받은 바이트 수(InputBytes)는 7Z 의 AES 코더 출력 크기로 기록되고,
/// 실제로 쓴 바이트 수는 16의 배수로 맞춰진다(<see cref="Finish"/> 에서 0 으로 채움). 안쪽 스트림은 닫지 않는다.
/// </summary>
internal sealed class AesEncryptStream : Stream
{
    private const int BlockSize = 16;
    private readonly Stream _inner;
    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _pending = new byte[64 * 1024];
    private readonly byte[] _output = new byte[64 * 1024];
    private int _pendingCount;
    private bool _finished;

    public AesEncryptStream(Stream inner, byte[] key, byte[] iv)
    {
        _inner = inner;
        _aes = Aes.Create();
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _encryptor = _aes.CreateEncryptor(key, iv);
    }

    public long InputBytes { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => InputBytes;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_finished)
            throw new InvalidOperationException(FormatText.Aes_StreamFinished);
        InputBytes += buffer.Length;
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, _pending.Length - _pendingCount);
            buffer[..n].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += n;
            buffer = buffer[n..];
            if (_pendingCount == _pending.Length)
                EncryptPending(_pendingCount);
        }
    }

    private void EncryptPending(int count)
    {
        int whole = count - count % BlockSize;
        if (whole == 0)
            return;
        int n = _encryptor.TransformBlock(_pending, 0, whole, _output, 0);
        _inner.Write(_output, 0, n);
        Buffer.BlockCopy(_pending, whole, _pending, 0, count - whole);
        _pendingCount = count - whole;
    }

    /// <summary>남은 바이트를 0 으로 채워 마지막 블록까지 암호화한다.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        int padded = (_pendingCount + BlockSize - 1) / BlockSize * BlockSize;
        Array.Clear(_pending, _pendingCount, padded - _pendingCount);
        EncryptPending(padded);
        _finished = true;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _encryptor.Dispose();
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_pending);
        }
        base.Dispose(disposing);
    }
}
