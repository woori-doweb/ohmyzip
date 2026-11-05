using System.Collections;
using System.ComponentModel;

namespace OhMyZip.App.Models;

/// <summary>목록 정렬: 항상 폴더 먼저, 그다음 선택한 칼럼(같으면 이름).</summary>
public sealed class EntryComparer(string member, ListSortDirection direction) : IComparer
{
    public string Member { get; } = member;
    public ListSortDirection Direction { get; } = direction;

    public int Compare(object? x, object? y)
    {
        if (x is not EntryRow a || y is not EntryRow b) return 0;
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        int c = Member switch
        {
            nameof(EntryRow.Size) => Nullable.Compare(a.Size, b.Size),
            nameof(EntryRow.CompressedSize) => Nullable.Compare(a.CompressedSize, b.CompressedSize),
            nameof(EntryRow.Modified) => Nullable.Compare(a.Modified, b.Modified),
            nameof(EntryRow.Crc32) => Nullable.Compare(a.Crc32, b.Crc32),
            nameof(EntryRow.TypeName) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRow.MethodText) => string.Compare(a.MethodText, b.MethodText, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRow.EncryptionText) => string.Compare(a.EncryptionText, b.EncryptionText, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        return Direction == ListSortDirection.Descending ? -c : c;
    }
}
