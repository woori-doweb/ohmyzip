namespace OhMyZip.Core;

/// <summary>ZIP/7Z 가 쓰는 CRC-32 (IEEE 802.3, 반사 다항식 0xEDB88320). .NET 9 ZipArchive 는 읽을 때 CRC 를 검증하지 않으므로 직접 계산한다.</summary>
internal sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private uint _state = 0xFFFFFFFFu;

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

    public void Append(ReadOnlySpan<byte> data)
    {
        uint c = _state;
        foreach (byte b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _state = c;
    }

    public uint Value => _state ^ 0xFFFFFFFFu;
}
