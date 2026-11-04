using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>암호가 없거나 틀렸을 때. 메시지에 암호를 넣지 않는다.</summary>
internal sealed class ZipPasswordException(bool missing) : Exception(missing ? MissingMessage : WrongMessage)
{
    public static string MissingMessage => EngineText.Zip_PasswordRequired;
    public static string WrongMessage => EngineText.Zip_PasswordWrong;

    public bool Missing { get; } = missing;
}

/// <summary>
/// WinZip AES(AE-1/AE-2) 상수와 키 유도. 소금 → PBKDF2-HMAC-SHA1(1000회) → AES 키 + HMAC 키 + 2바이트 검증값.
/// 데이터는 AES-CTR(리틀 엔디언 128비트 카운터, 1부터)로 암호화하고 암호문의 HMAC-SHA1 앞 10바이트를 붙인다.
/// 암호 바이트는 UTF-8 이다(ASCII 암호는 어느 인코딩에서도 같은 바이트).
/// </summary>
internal static class ZipAes
{
    public const ushort ExtraId = 0x9901;
    public const ushort Method = 99;
    public const int MacLength = 10;
    public const int VerifierLength = 2;
    private const int Iterations = 1000;

    /// <summary>강도 1/2/3 → AES-128/192/256 키 길이(바이트).</summary>
    public static int KeyLength(byte strength) => strength switch
    {
        1 => 16,
        2 => 24,
        3 => 32,
        _ => throw new NotSupportedException(string.Format(EngineText.Aes_UnknownStrength, strength)),
    };

    public static int SaltLength(byte strength) => KeyLength(strength) / 2;

    public static (byte[] Key, byte[] MacKey, byte[] Verifier) Derive(string password, byte[] salt, int keyLength)
    {
        byte[] material = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA1, 2 * keyLength + VerifierLength);
        return (material[..keyLength], material[keyLength..(2 * keyLength)], material[(2 * keyLength)..]);
    }

    /// <summary>0x9901 확장 필드(머리 4바이트 포함 11바이트): 공급자 버전 2(AE-2), "AE", 강도 3(AES-256), 실제 압축 방식.</summary>
    public static byte[] ExtraField(ushort actualMethod)
    {
        byte[] extra = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, ExtraId);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(4), 2);
        extra[6] = (byte)'A';
        extra[7] = (byte)'E';
        extra[8] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(9), actualMethod);
        return extra;
    }
}

/// <summary>WinZip 방식 AES-CTR. 카운터는 16바이트 리틀 엔디언으로 1부터 센다. 암호화·복호화가 같은 XOR 이다.</summary>
internal sealed class AesCtr : IDisposable
{
    private const int Blocks = 256;
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _counterBlocks = new byte[16 * Blocks];
    private readonly byte[] _keystream = new byte[16 * Blocks];
    private int _position = 16 * Blocks;

    public AesCtr(byte[] key) => _aes.Key = key;

    public void Transform(Span<byte> data)
    {
        while (data.Length > 0)
        {
            if (_position == _keystream.Length)
                Refill();
            int n = Math.Min(data.Length, _keystream.Length - _position);
            ReadOnlySpan<byte> ks = _keystream.AsSpan(_position, n);
            for (int i = 0; i < n; i++)
                data[i] ^= ks[i];
            _position += n;
            data = data[n..];
        }
    }

    private void Refill()
    {
        for (int b = 0; b < Blocks; b++)
        {
            for (int i = 0; i < 16 && ++_counter[i] == 0; i++)
            {
            }
            _counter.CopyTo(_counterBlocks, b * 16);
        }
        _aes.EncryptEcb(_counterBlocks, _keystream, PaddingMode.None);
        _position = 0;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>AES 항목 쓰기: 만들 때 소금·검증값을 먼저 쓰고, 받은 평문을 암호화해 내보내며, <see cref="Finish"/> 에서 인증 코드를 붙인다. 안쪽 스트림은 닫지 않는다.</summary>
internal sealed class WinZipAesWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly AesCtr _ctr;
    private readonly IncrementalHash _mac;
    private readonly byte[] _scratch = new byte[81920];

    public WinZipAesWriteStream(Stream inner, string password)
    {
        _inner = inner;
        byte[] salt = RandomNumberGenerator.GetBytes(ZipAes.SaltLength(3));
        var (key, macKey, verifier) = ZipAes.Derive(password, salt, ZipAes.KeyLength(3));
        _ctr = new AesCtr(key);
        _mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, macKey);
        _inner.Write(salt);
        _inner.Write(verifier);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, _scratch.Length);
            Span<byte> chunk = _scratch.AsSpan(0, n);
            buffer[..n].CopyTo(chunk);
            _ctr.Transform(chunk);
            _mac.AppendData(chunk);
            _inner.Write(chunk);
            buffer = buffer[n..];
        }
    }

    /// <summary>암호문 HMAC-SHA1 의 앞 10바이트를 쓴다.</summary>
    public void Finish() => _inner.Write(_mac.GetHashAndReset().AsSpan(0, ZipAes.MacLength));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctr.Dispose();
            _mac.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// AES 항목 읽기. 열 때 검증값으로 암호를 확인하고(틀리면 <see cref="ZipPasswordException"/>),
/// 암호문을 다 읽는 순간 인증 코드(HMAC)를 확인한다(틀리면 InvalidDataException — 손상 또는 변조).
/// </summary>
internal sealed class WinZipAesReadStream : Stream
{
    public static string MacMismatch => EngineText.Aes_MacMismatch;

    private readonly Stream _inner;
    private readonly AesCtr _ctr;
    private readonly IncrementalHash _mac;
    private long _remaining;
    private bool _verified;

    private WinZipAesReadStream(Stream inner, AesCtr ctr, IncrementalHash mac, long cipherLength)
    {
        _inner = inner;
        _ctr = ctr;
        _mac = mac;
        _remaining = cipherLength;
    }

    /// <summary>raw 는 항목의 압축 데이터 전체(소금+검증값+암호문+인증 코드, 길이 compressedSize).</summary>
    public static WinZipAesReadStream Open(Stream raw, long compressedSize, byte strength, string password)
    {
        int saltLength = ZipAes.SaltLength(strength);
        long cipherLength = compressedSize - saltLength - ZipAes.VerifierLength - ZipAes.MacLength;
        if (cipherLength < 0)
            throw new InvalidDataException(EngineText.Aes_EntryTooShort);
        byte[] salt = new byte[saltLength];
        byte[] stored = new byte[ZipAes.VerifierLength];
        raw.ReadExactly(salt);
        raw.ReadExactly(stored);
        var (key, macKey, verifier) = ZipAes.Derive(password, salt, ZipAes.KeyLength(strength));
        if (!CryptographicOperations.FixedTimeEquals(verifier, stored))
            throw new ZipPasswordException(missing: false);
        var stream = new WinZipAesReadStream(raw, new AesCtr(key), IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, macKey), cipherLength);
        if (cipherLength == 0)
            stream.Verify();
        return stream;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0 || buffer.Length == 0)
            return 0;
        int want = (int)Math.Min(buffer.Length, _remaining);
        int n = _inner.Read(buffer[..want]);
        if (n <= 0)
            throw new InvalidDataException(EngineText.Aes_EntryTruncated);
        _mac.AppendData(buffer[..n]);
        _ctr.Transform(buffer[..n]);
        _remaining -= n;
        if (_remaining == 0)
            Verify();
        return n;
    }

    /// <summary>압축 해제기가 끝까지 읽지 않았어도 남은 암호문을 소비해 인증 코드를 확인한다.</summary>
    public void Drain()
    {
        byte[] scratch = new byte[8192];
        while (Read(scratch) > 0)
        {
        }
    }

    private void Verify()
    {
        if (_verified)
            return;
        byte[] stored = new byte[ZipAes.MacLength];
        _inner.ReadExactly(stored);
        byte[] actual = _mac.GetHashAndReset();
        _verified = true;
        if (!CryptographicOperations.FixedTimeEquals(actual.AsSpan(0, ZipAes.MacLength), stored))
            throw new InvalidDataException(MacMismatch);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ctr.Dispose();
            _mac.Dispose();
            _inner.Dispose();
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

/// <summary>
/// 전통 PKWARE 암호(ZipCrypto) 복호화 전용. 다른 프로그램이 만든 압축 파일을 읽을 때만 쓰고, 새로 만들 때는 쓰지 않는다(약한 암호).
/// 12바이트 암호 머리의 마지막 바이트로 암호를 1차 확인한다(틀리면 <see cref="ZipPasswordException"/>). 최종 확인은 CRC 다.
/// </summary>
internal sealed class ZipCryptoReadStream : Stream
{
    private const int HeaderLength = 12;
    // CRC-32 표(Crc32.cs 와 같은 다항식). 키 갱신에 바이트 단위 갱신이 필요해 따로 둔다.
    private static readonly uint[] CrcTable = BuildTable();

    private readonly Stream _inner;
    private uint _key0 = 0x12345678, _key1 = 0x23456789, _key2 = 0x34567890;
    private long _remaining;

    private ZipCryptoReadStream(Stream inner, long remaining)
    {
        _inner = inner;
        _remaining = remaining;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>check 는 CRC 최상위 바이트(데이터 설명자가 있으면 DOS 시간의 상위 바이트).</summary>
    public static ZipCryptoReadStream Open(Stream raw, long compressedSize, string password, byte check)
    {
        if (compressedSize < HeaderLength)
            throw new InvalidDataException(EngineText.ZipCrypto_EntryTooShort);
        var stream = new ZipCryptoReadStream(raw, compressedSize - HeaderLength);
        foreach (byte b in Encoding.UTF8.GetBytes(password))
            stream.Update(b);
        byte[] header = new byte[HeaderLength];
        raw.ReadExactly(header);
        stream.Decrypt(header);
        if (header[HeaderLength - 1] != check)
            throw new ZipPasswordException(missing: false);
        return stream;
    }

    private void Update(byte b)
    {
        _key0 = CrcTable[(_key0 ^ b) & 0xFF] ^ (_key0 >> 8);
        _key1 = (_key1 + (_key0 & 0xFF)) * 134775813 + 1;
        _key2 = CrcTable[(_key2 ^ (_key1 >> 24)) & 0xFF] ^ (_key2 >> 8);
    }

    private void Decrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            uint temp = (_key2 | 2) & 0xFFFF;
            byte plain = (byte)(data[i] ^ (byte)((temp * (temp ^ 1)) >> 8));
            Update(plain);
            data[i] = plain;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0 || buffer.Length == 0)
            return 0;
        int n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        if (n <= 0)
            throw new InvalidDataException(EngineText.ZipCrypto_EntryTruncated);
        Decrypt(buffer[..n]);
        _remaining -= n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
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
