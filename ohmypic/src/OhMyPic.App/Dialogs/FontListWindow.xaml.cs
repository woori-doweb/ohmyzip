using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs;

/// <summary>One font in the list: names, the family used to draw the preview, and the favourite state.</summary>
public sealed class FontRow(FontEntry entry, bool isFavorite) : INotifyPropertyChanged
{
    private bool _isFavorite = isFavorite;

    public FontEntry Entry { get; } = entry;

    public string FamilyName => Entry.FamilyName;

    public string DisplayName => Entry.DisplayName;

    /// <summary>The invariant family name is shown next to the localized one only when they differ.</summary>
    public Visibility FamilyNameVisibility =>
        string.Equals(Entry.DisplayName, Entry.FamilyName, StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;

    public FontFamily Family { get; } = new(entry.FamilyName);

    public string FavoriteName { get; } = string.Format(CultureInfo.CurrentCulture, FontText.Favorite_Name, entry.FamilyName);

    public string FavoriteStatus => _isFavorite ? FontText.Favorite_Status : "";

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Tools &gt; Fonts… (design P16): installed fonts with search, Hangul and favourites filters, and a preview sentence drawn in
/// each font. Non-modal; the main window keeps a single instance. Favourites are saved on every change; the last search,
/// filters, preview text and size are remembered only for this process.
/// </summary>
public partial class FontListWindow : Window
{
    public static readonly DependencyProperty PreviewTextProperty =
        DependencyProperty.Register(nameof(PreviewText), typeof(string), typeof(FontListWindow), new PropertyMetadata(""));

    public static readonly DependencyProperty PreviewSizeProperty =
        DependencyProperty.Register(nameof(PreviewSize), typeof(double), typeof(FontListWindow), new PropertyMetadata(DefaultSize));

    private const double DefaultSize = 32;
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    // Last choices, kept in memory for the rest of the process (no settings keys).
    private static string s_query = "";
    private static bool s_hangulOnly;
    private static bool s_favoritesOnly;
    private static string? s_previewText;
    private static double s_previewSize = DefaultSize;

    private readonly FontFavorites _favorites = FontFavorites.Load();
    private readonly DispatcherTimer _filterTimer = new() { Interval = FilterDelay };
    private Dictionary<string, FontRow>? _rowsByFamily;
    private IReadOnlyList<FontRow> _allRows = [];
    private IReadOnlyList<FontRow> _shown = [];
    private bool _closed;
    private bool _rollingBack;

    public FontListWindow()
    {
        InitializeComponent();
        SearchBox.Text = s_query;
        HangulOnlyBox.IsChecked = s_hangulOnly;
        FavoritesOnlyBox.IsChecked = s_favoritesOnly;
        SampleBox.Text = s_previewText ?? FontText.Sample_Default;
        SizeSlider.Value = s_previewSize;
        PreviewText = SampleBox.Text;
        PreviewSize = SizeSlider.Value;
        CountText.Text = FontText.Loading;

        _filterTimer.Tick += (_, _) => ApplyFilter();
        SearchBox.TextChanged += (_, _) => ScheduleFilter();
        SearchBox.PreviewKeyDown += OnSearchKeyDown;
        // Checked/Unchecked (not Click) so a change from any source, including assistive technology, filters the list.
        RoutedEventHandler filterBoxChanged = (_, _) => ScheduleFilter();
        HangulOnlyBox.Checked += filterBoxChanged;
        HangulOnlyBox.Unchecked += filterBoxChanged;
        FavoritesOnlyBox.Checked += filterBoxChanged;
        FavoritesOnlyBox.Unchecked += filterBoxChanged;
        SampleBox.TextChanged += (_, _) => PreviewText = SampleBox.Text;
        SizeSlider.ValueChanged += (_, e) => PreviewSize = Math.Round(e.NewValue);
        FontList.PreviewKeyDown += OnListKeyDown;
        PreviewKeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosedWindow;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    /// <summary>Text every row draws in its own font; empty = each row shows its font name.</summary>
    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    /// <summary>Preview font size in DIP (12..72).</summary>
    public double PreviewSize
    {
        get => (double)GetValue(PreviewSizeProperty);
        set => SetValue(PreviewSizeProperty, value);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        // Enumerating every installed family and reading its cmap takes a moment: keep the window responsive.
        CultureInfo culture = CultureInfo.CurrentUICulture;
        IReadOnlyList<FontEntry> entries;
        try
        {
            entries = await Task.Run(() => FontCatalog.Load(culture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException or FormatException)
        {
            if (!_closed) CountText.Text = string.Format(CultureInfo.CurrentCulture, FontText.LoadFailed, ex.Message);
            return;
        }
        if (_closed) return;
        _allRows = entries.Select(entry => new FontRow(entry, _favorites.Contains(entry.FamilyName))).ToList();
        foreach (FontRow row in _allRows) row.PropertyChanged += OnRowPropertyChanged;
        _rowsByFamily = _allRows.ToDictionary(r => r.FamilyName, StringComparer.OrdinalIgnoreCase);
        FontList.IsEnabled = true;
        ApplyFilter();
    }

    private void OnClosedWindow(object? sender, EventArgs e)
    {
        _closed = true;
        _filterTimer.Stop();
        s_query = SearchBox.Text;
        s_hangulOnly = HangulOnlyBox.IsChecked == true;
        s_favoritesOnly = FavoritesOnlyBox.IsChecked == true;
        s_previewText = SampleBox.Text;
        s_previewSize = SizeSlider.Value;
    }

    private void ScheduleFilter()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>Shows the rows matching the search box and both filters, keeping the selected font when it still matches.</summary>
    private void ApplyFilter()
    {
        _filterTimer.Stop();
        if (_rowsByFamily is null) return;
        bool favoritesOnly = FavoritesOnlyBox.IsChecked == true;
        List<FontRow> rows = FontCatalog.Filter(_allRows.Select(r => r.Entry), SearchBox.Text, HangulOnlyBox.IsChecked == true)
            .Select(entry => _rowsByFamily[entry.FamilyName])
            .Where(row => !favoritesOnly || row.IsFavorite)
            .ToList();
        UpdateCount(rows.Count);
        if (rows.SequenceEqual(_shown)) return;

        bool hadFocus = FontList.IsKeyboardFocusWithin;
        var selected = FontList.SelectedItem as FontRow;
        int selectedIndex = FontList.SelectedIndex;
        _shown = rows;
        FontList.ItemsSource = rows;
        if (selected is not null && rows.Contains(selected))
            FontList.SelectedItem = selected;
        else if (selectedIndex >= 0 && rows.Count > 0)
            FontList.SelectedIndex = Math.Min(selectedIndex, rows.Count - 1);
        if (FontList.SelectedItem is { } item)
        {
            FontList.ScrollIntoView(item);
            if (hadFocus) FocusSelectedRow();
        }
        else if (hadFocus)
        {
            SearchBox.Focus();
        }
    }

    private void UpdateCount(int count)
    {
        CountText.Text = count == 1 ? FontText.Count_One : string.Format(CultureInfo.CurrentCulture, FontText.Count_Many, count);
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusSelectedRow()
    {
        if (FontList.SelectedItem is not { } item) return;
        FontList.ScrollIntoView(item);
        FontList.UpdateLayout();
        if (FontList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();
        else
            FontList.Focus();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Down from the search box moves into the list.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None || !FontList.IsEnabled) return;
        ApplyFilter(); // run a pending search now so Down lands on the current results
        if (FontList.Items.Count == 0) return;
        if (FontList.SelectedIndex < 0) FontList.SelectedIndex = 0;
        FocusSelectedRow();
        e.Handled = true;
    }

    /// <summary>Space toggles the favourite of the selected font; Ctrl+C copies its family name.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (FontList.SelectedItem is not FontRow row) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            row.IsFavorite = !row.IsFavorite; // saved by OnRowPropertyChanged
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            CopyFamilyName(row);
        }
    }

    /// <summary>A pointer click on a star also selects its row; the favourite itself is saved by <see cref="OnRowPropertyChanged"/>.</summary>
    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FontRow row }) FontList.SelectedItem = row;
    }

    /// <summary>
    /// Every change of <see cref="FontRow.IsFavorite"/> (star click, UIA toggle, Space) is saved. The save runs after the
    /// current binding update has finished, so a failed save can undo the value and show the error outside of it.
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rollingBack || e.PropertyName != nameof(FontRow.IsFavorite) || sender is not FontRow row) return;
        Dispatcher.BeginInvoke(() => CommitFavorite(row));
    }

    /// <summary>Stores the row's favourite state; when saving fails the change is undone and reported.</summary>
    private void CommitFavorite(FontRow row)
    {
        bool favorite = row.IsFavorite;
        bool changed = favorite ? _favorites.Add(row.FamilyName) : _favorites.Remove(row.FamilyName);
        if (!changed) return;
        try
        {
            _favorites.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (favorite) _favorites.Remove(row.FamilyName);
            else _favorites.Add(row.FamilyName);
            _rollingBack = true;
            try
            {
                row.IsFavorite = !favorite;
            }
            finally
            {
                _rollingBack = false;
            }
            MessageDialog.Error(this, FontText.FavoritesSaveFailed_Title,
                string.Format(CultureInfo.CurrentCulture, FontText.FavoritesSaveFailed_Body, ex.Message));
            return;
        }
        if (!favorite && FavoritesOnlyBox.IsChecked == true) ApplyFilter();
    }

    private void CopyFamilyName(FontRow row)
    {
        try
        {
            Clipboard.SetText(row.FamilyName);
        }
        catch (ExternalException ex)
        {
            MessageDialog.Error(this, MainText.Clipboard_FailedTitle,
                string.Format(CultureInfo.CurrentCulture, MainText.Error_ReasonWithDetail, MainText.Clipboard_FailedReason, ex.Message));
        }
    }
}
