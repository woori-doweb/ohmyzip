using System.Windows.Input;
using OhMyPic.App.Text;

namespace OhMyPic.App.Commands;

/// <summary>
/// One command row. <see cref="Key"/> is the traceability command key (data/ohmypic/traceability.json "command").
/// <see cref="Gesture"/> is the text shown in the menu; <see cref="Keys"/> are the bound key chords
/// (null = parse <see cref="Gesture"/>, empty = no binding). Unimplemented rows stay visible, disabled, with the
/// "(Coming soon)" suffix.
/// </summary>
public sealed record CommandSpec(
    string Key,
    string Menu,
    string Label,
    string? Gesture = null,
    string[]? Keys = null,
    bool Implemented = true,
    bool SeparatorBefore = false,
    bool Checkable = false);

/// <summary>Toolbar item. <see cref="DropDown"/> set = a button that opens a menu of those command keys.</summary>
public sealed record ToolbarSpec(string Key, string Label, string Glyph, IReadOnlyList<string?>? DropDown = null);

/// <summary>
/// The single command table. Menus, toolbars, the tile context menu and key bindings are all generated from it
/// (OP-A04/OP-A05: every toolbar item is also a menu command). Enable rules live in MainWindow.CanRun.
/// </summary>
public static class CommandCatalog
{
    public static readonly string PendingSuffix = MenuText.PendingSuffix;

    private static class M
    {
        public const string File = "file";
        public const string Edit = "edit";
        public const string View = "view";
        public const string Image = "image";
        public const string Slideshow = "slideshow";
        public const string Tools = "tools";
        public const string Help = "help";
    }

    /// <summary>The seven top-level menus in display order (design P02).</summary>
    public static readonly IReadOnlyList<(string Id, string Header)> Menus =
    [
        (M.File, MenuText.Menu_File),
        (M.Edit, MenuText.Menu_Edit),
        (M.View, MenuText.Menu_View),
        (M.Image, MenuText.Menu_Image),
        (M.Slideshow, MenuText.Menu_Slideshow),
        (M.Tools, MenuText.Menu_Tools),
        (M.Help, MenuText.Menu_Help),
    ];

    /// <summary>All commands in menu order. No submenus (design P12).</summary>
    public static readonly IReadOnlyList<CommandSpec> Items =
    [
        // File
        new("file.openFile", M.File, MenuText.Label_OpenFile, "Ctrl+O"),
        new("file.openFolder", M.File, MenuText.Label_OpenFolder, "Ctrl+Shift+O"),
        new("file.saveAs", M.File, MenuText.Label_SaveAs, "Ctrl+S", SeparatorBefore: true),
        new("file.saveRotation", M.File, MenuText.Label_SaveRotation, null),
        new("file.print", M.File, MenuText.Label_Print, "Ctrl+P", SeparatorBefore: true),
        new("file.properties", M.File, MenuText.Label_Properties, "Alt+Enter", SeparatorBefore: true),
        new("file.exit", M.File, MenuText.Label_Exit, "Alt+F4", Keys: [], SeparatorBefore: true),

        // Edit
        new("edit.copy", M.Edit, MenuText.Label_Copy, "Ctrl+C"),
        new("edit.cut", M.Edit, MenuText.Label_Cut, "Ctrl+X"),
        new("edit.paste", M.Edit, MenuText.Label_Paste, "Ctrl+V"),
        new("edit.copyImage", M.Edit, MenuText.Label_CopyImage, "Ctrl+Shift+C", SeparatorBefore: true),
        new("edit.delete", M.Edit, MenuText.Label_Delete, "Del", SeparatorBefore: true),
        new("edit.rename", M.Edit, MenuText.Label_Rename, "F2"),
        new("edit.selectAll", M.Edit, MenuText.Label_SelectAll, "Ctrl+A", SeparatorBefore: true),

        // View: mode switch, view formats, panes, sort, zoom, navigation, window
        new("view.browser", M.View, MenuText.Label_Browser, "Esc", Keys: ["Escape"]),
        new("view.viewer", M.View, MenuText.Label_Viewer, "Enter"),
        new("view.format.thumbnails", M.View, MenuText.Label_FormatThumbnails, SeparatorBefore: true, Checkable: true),
        new("view.format.largeIcons", M.View, MenuText.Label_FormatLargeIcons, Checkable: true),
        new("view.format.icons", M.View, MenuText.Label_FormatIcons, Checkable: true),
        new("view.format.list", M.View, MenuText.Label_FormatList, Checkable: true),
        new("view.format.details", M.View, MenuText.Label_FormatDetails, Checkable: true),
        new("view.format.filmstrip", M.View, MenuText.Label_FormatFilmstrip, Checkable: true),
        new("view.pane.preview", M.View, MenuText.Label_PanePreview, SeparatorBefore: true, Checkable: true),
        new("view.pane.camera", M.View, MenuText.Label_PaneCamera, Checkable: true),
        new("view.folderTree", M.View, MenuText.Label_FolderTree, Checkable: true),
        new("view.sort.name", M.View, MenuText.Label_SortName, SeparatorBefore: true, Checkable: true),
        new("view.sort.type", M.View, MenuText.Label_SortType, Checkable: true),
        new("view.sort.size", M.View, MenuText.Label_SortSize, Checkable: true),
        new("view.sort.date", M.View, MenuText.Label_SortDate, Checkable: true),
        new("view.sort.ascending", M.View, MenuText.Label_SortAscending, SeparatorBefore: true, Checkable: true),
        new("view.sort.descending", M.View, MenuText.Label_SortDescending, Checkable: true),
        new("nav.up", M.View, MenuText.Label_Up, "Alt+Up", Keys: ["Alt+Up", "Back"], SeparatorBefore: true),
        new("view.refresh", M.View, MenuText.Label_Refresh, "Ctrl+F5"),
        new("zoom.in", M.View, MenuText.Label_ZoomIn, "+", Keys: ["OemPlus", "Shift+OemPlus", "Add", "Ctrl+OemPlus", "Ctrl+Add"], SeparatorBefore: true),
        new("zoom.out", M.View, MenuText.Label_ZoomOut, "-", Keys: ["OemMinus", "Subtract", "Ctrl+OemMinus", "Ctrl+Subtract"]),
        new("zoom.actual", M.View, MenuText.Label_ZoomActual, "0", Keys: ["D0", "NumPad0", "Ctrl+D0", "Ctrl+NumPad0"]),
        new("zoom.fit", M.View, MenuText.Label_ZoomFit, "*", Keys: ["Multiply", "Shift+D8"]),
        new("nav.previous", M.View, MenuText.Label_Previous, "Left", Keys: ["Left", "PageUp", "Back"], SeparatorBefore: true),
        new("nav.next", M.View, MenuText.Label_Next, "Right", Keys: ["Right", "PageDown", "Space"]),
        new("nav.first", M.View, MenuText.Label_First, "Home"),
        new("nav.last", M.View, MenuText.Label_Last, "End"),
        new("view.fullScreen", M.View, MenuText.Label_FullScreen, "F11", Keys: ["F11", "Enter"], SeparatorBefore: true, Checkable: true),
        new("view.alwaysOnTop", M.View, MenuText.Label_AlwaysOnTop, Checkable: true),

        // Image (edit submenu raised to top level, design P12)
        new("image.rotateRight", M.Image, MenuText.Label_RotateRight, "Ctrl+]", Keys: ["Ctrl+OemCloseBrackets"]),
        new("image.rotateLeft", M.Image, MenuText.Label_RotateLeft, "Ctrl+[", Keys: ["Ctrl+OemOpenBrackets"]),
        new("image.rotateDialog", M.Image, MenuText.Label_RotateDialog, "Ctrl+T", SeparatorBefore: true),
        new("image.resizeDialog", M.Image, MenuText.Label_ResizeDialog, "Ctrl+R"),
        new("image.convertDialog", M.Image, MenuText.Label_ConvertDialog, "Ctrl+E"),
        new("image.batchDialog", M.Image, MenuText.Label_BatchDialog, "Ctrl+M"),

        // Slideshow
        new("slideshow.start", M.Slideshow, MenuText.Label_SlideshowStart, "F5"),

        // Tools
        new("tools.settings", M.Tools, MenuText.Label_Settings, "F4"),
        new("settings.view.fitMode", M.Tools, MenuText.Label_FitModeSettings),
        new("settings.language", M.Tools, MenuText.Label_Language),
        new("settings.associations", M.Tools, MenuText.Label_Associations, SeparatorBefore: true),
        new("settings.shellMenu", M.Tools, MenuText.Label_ShellMenu),

        // Help
        new("help.contents", M.Help, MenuText.Label_HelpContents, "F1"),
        new("help.update", M.Help, UpdateText.Menu_CheckForUpdates, SeparatorBefore: true),
        new("help.about", M.Help, MenuText.Label_About),
    ];

    /// <summary>Browser toolbar, three groups (design P03): view/slideshow, file operations, view format/sort.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<ToolbarSpec>> BrowserToolbar =
    [
        [new("view.viewer", MenuText.Toolbar_Viewer, "\uE8B9"), new("slideshow.start", MenuText.Toolbar_Slideshow, "\uE786")],
        [
            new("edit.copy", MenuText.Toolbar_Copy, "\uE8C8"), new("edit.cut", MenuText.Toolbar_Cut, "\uE8C6"),
            new("edit.paste", MenuText.Toolbar_Paste, "\uE77F"), new("edit.delete", MenuText.Toolbar_Delete, "\uE74D"),
            new("edit.rename", MenuText.Toolbar_Rename, "\uE8AC"),
        ],
        [
            new("toolbar.viewFormat", MenuText.Toolbar_ViewFormat, "\uE8A9",
                ["view.format.thumbnails", "view.format.largeIcons", "view.format.icons", "view.format.list", "view.format.details", "view.format.filmstrip",
                 null, "view.pane.preview", "view.pane.camera"]),
            new("toolbar.sort", MenuText.Toolbar_Sort, "\uE8CB",
                ["view.sort.name", "view.sort.type", "view.sort.size", "view.sort.date", null, "view.sort.ascending", "view.sort.descending"]),
        ],
    ];

    /// <summary>Viewer toolbar, three groups (design P03): list, zoom/full screen, rotate/slideshow/edit.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<ToolbarSpec>> ViewerToolbar =
    [
        [new("view.browser", MenuText.Toolbar_Browser, "\uE8FD")],
        [
            new("zoom.in", MenuText.Toolbar_ZoomIn, "\uE8A3"), new("zoom.out", MenuText.Toolbar_ZoomOut, "\uE71F"),
            new("zoom.actual", MenuText.Toolbar_ZoomActual, "\uE799"), new("zoom.fit", MenuText.Toolbar_ZoomFit, "\uE9A6"),
            new("view.fullScreen", MenuText.Toolbar_FullScreen, "\uE740"),
        ],
        [
            new("image.rotateLeft", MenuText.Toolbar_RotateLeft, "\uE7A7"), new("image.rotateRight", MenuText.Toolbar_RotateRight, "\uE7A6"),
            new("slideshow.start", MenuText.Toolbar_Slideshow, "\uE786"),
            new("toolbar.edit", MenuText.Toolbar_Edit, "\uE70F",
                ["image.rotateDialog", "image.resizeDialog", "image.convertDialog", "image.batchDialog"]),
        ],
    ];

    /// <summary>Tile context menu in the observed order (null = separator).</summary>
    public static readonly IReadOnlyList<string?> TileContextMenu =
    [
        "view.viewer", "slideshow.start", null,
        "edit.copy", "edit.copyImage", "edit.cut", "edit.delete", "edit.rename", null,
        "image.rotateDialog", "image.resizeDialog", "image.convertDialog", null,
        "file.print", "file.properties",
    ];

    private static readonly Dictionary<string, CommandSpec> ByKey = Items.ToDictionary(i => i.Key, StringComparer.Ordinal);

    private static readonly Dictionary<string, RoutedUICommand> Routed = Items
        .Where(i => i.Implemented)
        .ToDictionary(i => i.Key, i => new RoutedUICommand(i.Label, i.Key, typeof(CommandCatalog)), StringComparer.Ordinal);

    /// <summary>One routed command for every key chord; the parameter is the list of command keys sharing that chord.</summary>
    public static readonly RoutedCommand Shortcut = new("shortcut", typeof(CommandCatalog));

    public static bool IsImplemented(string key) => ByKey.TryGetValue(key, out var s) && s.Implemented;

    public static IEnumerable<string> ImplementedKeys => Routed.Keys;

    public static CommandSpec Spec(string key) =>
        ByKey.TryGetValue(key, out var s) ? s : throw new KeyNotFoundException(string.Format(MenuText.Error_UnknownCommand, key));

    /// <summary>Routed command of an implemented key. Unknown or unimplemented keys throw.</summary>
    public static RoutedUICommand Command(string key) =>
        Routed.TryGetValue(key, out var cmd) ? cmd : throw new KeyNotFoundException(string.Format(MenuText.Error_UnknownCommand, key));

    /// <summary>
    /// Distinct key chords of implemented commands, each with the command keys bound to it in table order
    /// (for example Enter = view.viewer in the browser, view.fullScreen in the viewer).
    /// </summary>
    public static IReadOnlyList<(KeyChord Chord, IReadOnlyList<string> Keys)> Chords()
    {
        var map = new Dictionary<(Key, ModifierKeys), (KeyChord Chord, List<string> Keys)>();
        var order = new List<(Key, ModifierKeys)>();
        foreach (var spec in Items.Where(i => i.Implemented))
        {
            string[] texts = spec.Keys ?? (spec.Gesture is null ? Array.Empty<string>() : new[] { spec.Gesture });
            foreach (var text in texts)
            {
                var chord = KeyChord.Parse(text);
                var id = (chord.Key, chord.Modifiers);
                if (!map.TryGetValue(id, out var entry))
                {
                    entry = (chord, new List<string>());
                    map[id] = entry;
                    order.Add(id);
                }
                entry.Keys.Add(spec.Key);
            }
        }
        return order.Select(id => (map[id].Chord, (IReadOnlyList<string>)map[id].Keys)).ToList();
    }
}

/// <summary>
/// A key + modifiers gesture without KeyGesture's validation (plain digits and letters are allowed, as in "0" = actual size).
/// Alt combinations arrive as Key.System and IME-processed keys as Key.ImeProcessed; both are unwrapped.
/// </summary>
public sealed class KeyChord(Key key, ModifierKeys modifiers) : InputGesture
{
    public Key Key { get; } = key;
    public ModifierKeys Modifiers { get; } = modifiers;

    public override bool Matches(object targetElement, InputEventArgs inputEventArgs)
    {
        if (inputEventArgs is not KeyEventArgs e) return false;
        var pressed = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        return pressed == Key && Keyboard.Modifiers == Modifiers;
    }

    /// <summary>"Ctrl+Shift+O", "Alt+Enter", "Del", "F2", "OemPlus", "D0" (Key enum names plus a few aliases).</summary>
    public static KeyChord Parse(string text)
    {
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "Ctrl": mods |= ModifierKeys.Control; break;
                case "Shift": mods |= ModifierKeys.Shift; break;
                case "Alt": mods |= ModifierKeys.Alt; break;
                case "Del": key = Key.Delete; break;
                case "Ins": key = Key.Insert; break;
                case "Esc": key = Key.Escape; break;
                case "Enter": key = Key.Return; break;
                default: key = Enum.Parse<Key>(part, ignoreCase: true); break;
            }
        }
        if (key == Key.None) throw new FormatException(string.Format(MenuText.Error_BadGesture, text));
        return new KeyChord(key, mods);
    }
}
