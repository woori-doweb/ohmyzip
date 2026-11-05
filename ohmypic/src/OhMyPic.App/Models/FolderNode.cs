using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace OhMyPic.App.Models;

/// <summary>
/// Folder tree node. Children load lazily on first expand (off the UI thread); until then a placeholder child
/// keeps the expander visible. Folders that cannot be listed simply show no children.
/// </summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private readonly Func<bool> _includeHidden;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;

    public FolderNode(string name, string path, Func<bool> includeHidden, string glyph = "\uE8B7")
    {
        Name = name;
        Path = path;
        Glyph = glyph;
        _includeHidden = includeHidden;
        Children.Add(CreatePlaceholder());
    }

    private FolderNode()
    {
        Name = "";
        Path = "";
        Glyph = "";
        _includeHidden = () => false;
        IsPlaceholder = true;
        _loaded = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Path { get; }
    public string Glyph { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            if (value && !_loaded) _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private static FolderNode CreatePlaceholder() => new();

    /// <summary>Forget loaded children (after the hidden-files setting changed); they reload on the next expand.</summary>
    public void Reset()
    {
        if (IsPlaceholder) return;
        _loaded = false;
        Children.Clear();
        Children.Add(CreatePlaceholder());
        if (_isExpanded) _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        bool hidden = _includeHidden();
        var path = Path;
        var names = await Task.Run(() => ListSubfolders(path, hidden));
        Children.Clear();
        foreach (var (name, full) in names) Children.Add(new FolderNode(name, full, _includeHidden));
    }

    private static List<(string Name, string Path)> ListSubfolders(string path, bool includeHidden)
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var dir in new DirectoryInfo(path).EnumerateDirectories())
            {
                try
                {
                    var attrs = dir.Attributes;
                    if (!includeHidden && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    result.Add((dir.Name, dir.FullName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Item1, b.Item1));
        return result;
    }
}
