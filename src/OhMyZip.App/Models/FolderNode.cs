using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OhMyZip.App.Models;

/// <summary>사이드바 트리 노드. 루트는 Path="" 이고 이름은 압축 파일 이름.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public FolderNode? Parent { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public string Glyph => Parent is null ? "\uE7B8" : "\uE8B7";

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public static FolderNode Build(ArchiveModel model)
    {
        var root = new FolderNode { Name = model.FileName, Path = "", IsExpanded = true };
        AddChildren(model, root);
        return root;
    }

    private static void AddChildren(ArchiveModel model, FolderNode node)
    {
        foreach (var sub in model.SubFolders(node.Path))
        {
            var child = new FolderNode { Name = ArchiveModel.LeafName(sub), Path = sub, Parent = node };
            node.Children.Add(child);
            AddChildren(model, child);
        }
    }

    public FolderNode? Find(string path)
    {
        if (Path == path) return this;
        if (Path.Length > 0 && !path.StartsWith(Path + "/", StringComparison.Ordinal)) return null;
        foreach (var c in Children)
        {
            var hit = c.Find(path);
            if (hit is not null) return hit;
        }
        return null;
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
