using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyPic.App.Settings;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs;

/// <summary>
/// Settings: General, View, Slideshow, Thumbnails, File Associations, File Explorer Menu (fixed order). OK writes the
/// controls of the first four pages into the given <see cref="AppSettings"/>; Cancel leaves it untouched. Reset Page /
/// Reset All restore engine defaults in the controls only. The last two pages change the current user's registry
/// immediately through their own buttons (OK and Cancel do not undo them).
/// </summary>
public partial class PreferencesWindow : Window
{
    public const int GeneralPage = 0;
    public const int ViewPage = 1;
    public const int SlideshowPage = 2;
    public const int ThumbnailsPage = 3;
    public const int AssociationsPage = 4;
    public const int ShellMenuPage = 5;

    /// <summary>Pages backed by <see cref="AppSettings"/> (Reset Page / Reset All / OK apply to these).</summary>
    private const int SettingsPageCount = 4;
    private const int PageCount = 6;

    private static readonly string[] BackgroundPresets = ["System", "#000000", "#202020", "#808080", "#FFFFFF"];

    private readonly AppSettings _settings;

    public PreferencesWindow(AppSettings settings, int page)
    {
        InitializeComponent();
        _settings = settings;
        CategoryList.ItemsSource = new[]
        {
            DialogText.Pref_Page_General, DialogText.Pref_Page_View, DialogText.Pref_Page_Slideshow, DialogText.Pref_Page_Thumbnails,
            DialogText.Pref_Page_Associations, DialogText.Pref_Page_ShellMenu,
        };

        Fill(RotateSaveBox, (RotateAutoSaveMode.Ask, DialogText.Pref_RotateAsk), (RotateAutoSaveMode.Overwrite, DialogText.Pref_RotateOverwrite));
        Fill(LanguageBox, (UiLanguage.SystemValue, DialogText.Pref_LanguageSystem), (UiLanguage.EnglishValue, DialogText.Pref_LanguageEnglish),
            (UiLanguage.KoreanValue, DialogText.Pref_LanguageKorean));
        Fill(FitModeBox, (FitMode.ShrinkLargeOnly, DialogText.Pref_FitShrinkLargeOnly), (FitMode.FitWindow, DialogText.Pref_FitWindow),
            (FitMode.FitWidth, DialogText.Pref_FitWidth), (FitMode.FitHeight, DialogText.Pref_FitHeight),
            (FitMode.ActualSize, DialogText.Pref_FitActualSize), (FitMode.KeepZoom, DialogText.Pref_FitKeepZoom));
        Fill(WheelBox, (WheelAction.NextPrevious, DialogText.Pref_WheelNextPrevious), (WheelAction.Zoom, DialogText.Pref_WheelZoom));

        IntervalSlider.ValueChanged += (_, _) => IntervalValue.Text = string.Format(DialogText.Pref_IntervalValue, (int)IntervalSlider.Value);
        ThumbSizeSlider.ValueChanged += (_, _) => ThumbSizeValue.Text = string.Format(DialogText.Pref_PixelsValue, (int)ThumbSizeSlider.Value);

        for (int p = 0; p < SettingsPageCount; p++) LoadPage(p, settings);
        InitSystemPages();
        CategoryList.SelectedIndex = Math.Clamp(page, 0, PageCount - 1);
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
        Loaded += (_, _) => (CategoryList.ItemContainerGenerator.ContainerFromIndex(CategoryList.SelectedIndex) as UIElement)?.Focus();
    }

    private static void Fill<T>(ComboBox box, params (T Value, string Label)[] items)
    {
        box.Items.Clear();
        foreach (var (value, label) in items) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    }

    private static void Select(ComboBox box, object value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, value))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static T Selected<T>(ComboBox box, T fallback) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;

    private void LoadPage(int page, AppSettings s)
    {
        switch (page)
        {
            case GeneralPage:
                DeleteWithoutAskingBox.IsChecked = s.DeleteWithoutAsking;
                UseRecycleBinBox.IsChecked = s.UseRecycleBin;
                ShowHiddenFilesBox.IsChecked = s.ShowHiddenFiles;
                Select(RotateSaveBox, s.RotateAutoSave);
                QualitySlider.Value = s.JpegQuality;
                ApplyLastQualityBox.IsChecked = s.ApplyLastSaveQuality;
                Select(LanguageBox, NormalizeLanguage(s.Language));
                CheckUpdatesBox.IsChecked = s.CheckForUpdates;
                break;
            case ViewPage:
                Select(FitModeBox, s.FitMode);
                Select(WheelBox, s.WheelAction);
                FillBackground(s.BackgroundColor);
                LoopBox.IsChecked = s.LoopAtEnds;
                AutoRotateBox.IsChecked = s.AutoRotateByExif;
                SmoothBox.IsChecked = s.SmoothScaling;
                NavigatorBox.IsChecked = s.ShowNavigator;
                TopmostBox.IsChecked = s.AlwaysOnTop;
                break;
            case SlideshowPage:
                IntervalSlider.Value = s.SlideIntervalSeconds;
                IntervalValue.Text = string.Format(DialogText.Pref_IntervalValue, s.SlideIntervalSeconds);
                FadeBox.IsChecked = s.SlideFade;
                FileNameBox.IsChecked = s.ShowFileNameInFullScreen;
                break;
            case ThumbnailsPage:
                ThumbSizeSlider.Value = s.ThumbnailSize;
                ThumbSizeValue.Text = string.Format(DialogText.Pref_PixelsValue, s.ThumbnailSize);
                ResolutionBox.IsChecked = s.ShowResolutionOnThumbnail;
                break;
        }
    }

    private static string NormalizeLanguage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        UiLanguage.EnglishValue => UiLanguage.EnglishValue,
        UiLanguage.KoreanValue => UiLanguage.KoreanValue,
        _ => UiLanguage.SystemValue,
    };

    /// <summary>Presets plus the current custom colour (if any) so a hand-edited value survives OK.</summary>
    private void FillBackground(string? current)
    {
        var items = new List<(string Value, string Label)>
        {
            ("System", DialogText.Pref_BackgroundSystem),
            ("#000000", DialogText.Pref_BackgroundBlack),
            ("#202020", DialogText.Pref_BackgroundDarkGray),
            ("#808080", DialogText.Pref_BackgroundGray),
            ("#FFFFFF", DialogText.Pref_BackgroundWhite),
        };
        var value = string.IsNullOrWhiteSpace(current) ? "System" : current.Trim();
        var match = BackgroundPresets.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            items.Add((value, string.Format(DialogText.Pref_BackgroundCustom, value)));
            match = value;
        }
        Fill(BackgroundBox, items.ToArray());
        Select(BackgroundBox, match);
    }

    private void Store(AppSettings s)
    {
        s.DeleteWithoutAsking = DeleteWithoutAskingBox.IsChecked == true;
        s.UseRecycleBin = UseRecycleBinBox.IsChecked == true;
        s.ShowHiddenFiles = ShowHiddenFilesBox.IsChecked == true;
        s.RotateAutoSave = Selected(RotateSaveBox, s.RotateAutoSave);
        s.JpegQuality = (int)Math.Round(QualitySlider.Value);
        s.ApplyLastSaveQuality = ApplyLastQualityBox.IsChecked == true;
        s.Language = Selected(LanguageBox, UiLanguage.SystemValue);
        s.CheckForUpdates = CheckUpdatesBox.IsChecked == true;

        s.FitMode = Selected(FitModeBox, s.FitMode);
        s.WheelAction = Selected(WheelBox, s.WheelAction);
        s.BackgroundColor = Selected(BackgroundBox, "System");
        s.LoopAtEnds = LoopBox.IsChecked == true;
        s.AutoRotateByExif = AutoRotateBox.IsChecked == true;
        s.SmoothScaling = SmoothBox.IsChecked == true;
        s.ShowNavigator = NavigatorBox.IsChecked == true;
        s.AlwaysOnTop = TopmostBox.IsChecked == true;

        s.SlideIntervalSeconds = (int)Math.Round(IntervalSlider.Value);
        s.SlideFade = FadeBox.IsChecked == true;
        s.ShowFileNameInFullScreen = FileNameBox.IsChecked == true;

        s.ThumbnailSize = (int)Math.Round(ThumbSizeSlider.Value);
        s.ShowResolutionOnThumbnail = ResolutionBox.IsChecked == true;
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        for (int i = 0; i < Pages.Children.Count; i++)
            Pages.Children[i].Visibility = i == CategoryList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        // The registry pages have no settings to reset.
        ResetPageButton.IsEnabled = CategoryList.SelectedIndex < SettingsPageCount;
    }

    private void OnResetPage(object sender, RoutedEventArgs e) => LoadPage(Math.Max(0, CategoryList.SelectedIndex), new AppSettings());

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        for (int p = 0; p < SettingsPageCount; p++) LoadPage(p, defaults);
    }

    // ───────────── File associations / File Explorer menu (applied immediately) ─────────────

    private readonly Dictionary<string, (CheckBox Box, TextBlock Status)> _assocRows = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _extensions = [];

    private void InitSystemPages()
    {
        _extensions = SystemIntegration.Extensions;
        foreach (string ext in _extensions)
        {
            string id = ext.TrimStart('.');
            var box = new CheckBox { Content = ext, MinWidth = 90 };
            AutomationProperties.SetAutomationId(box, "OP-Pref-Assoc-Ext-" + id);
            box.Checked += (_, _) => UpdateAssocButtons();
            box.Unchecked += (_, _) => UpdateAssocButtons();
            var status = new TextBlock { Margin = new Thickness(8, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            status.SetResourceReference(TextBlock.ForegroundProperty, Theme.ThemeManager.SecondaryText);
            AutomationProperties.SetAutomationId(status, "OP-Pref-Assoc-ExtStatus-" + id);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(box);
            row.Children.Add(status);
            AssocList.Children.Add(row);
            _assocRows[ext] = (box, status);
        }
        ShellItemsText.Text = string.Format(DialogText.Pref_Shell_Items, string.Join(", ", ShellMenu.Commands.Select(SystemIntegration.ShellLabel)));
        ShellExtensionsText.Text = string.Format(DialogText.Pref_Shell_Extensions, string.Join(", ", _extensions));
        RefreshAssocStatus(initChecks: true);
        RefreshShellStatus();
    }

    /// <summary>Lets a screen reader read a status line changed in place of an alert (LiveSetting=Polite).</summary>
    private static void Announce(TextBlock status) =>
        (UIElementAutomationPeer.FromElement(status) ?? UIElementAutomationPeer.CreatePeerForElement(status))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private void UpdateAssocButtons() =>
        AssocRegisterButton.IsEnabled = SystemIntegration.ExePath is not null && _assocRows.Values.Any(r => r.Box.IsChecked == true);

    /// <summary>
    /// Re-reads every extension's state. initChecks: check the registered extensions (all of them when none is registered).
    /// Unregister stays available whenever something may be registered or the state cannot be read.
    /// </summary>
    private void RefreshAssocStatus(bool initChecks = false)
    {
        string? exe = SystemIntegration.ExePath;
        if (exe is null)
        {
            AssocStatusText.Text = DialogText.Sys_ExePathUnknown;
            foreach (var row in _assocRows.Values) row.Status.Text = "";
            if (initChecks) foreach (var row in _assocRows.Values) row.Box.IsChecked = true;
            AssocUnregisterButton.IsEnabled = true;
            UpdateAssocButtons();
            return;
        }
        try
        {
            var states = FileAssociations.Status(Registry.CurrentUser, exe, FileAssociations.CandidateExtensions);
            int registered = 0;
            foreach (string ext in _extensions)
            {
                var state = states[ext];
                var (box, status) = _assocRows[ext];
                status.Text = state switch
                {
                    FileAssociationStatus.Registered => DialogText.Pref_Assoc_StatusRegistered,
                    FileAssociationStatus.RegisteredElsewhere => DialogText.Pref_Assoc_StatusElsewhere,
                    _ => DialogText.Pref_Assoc_StatusNotRegistered,
                };
                status.ToolTip = status.Text;
                AutomationProperties.SetItemStatus(box, status.Text);
                if (state == FileAssociationStatus.Registered) registered++;
            }
            bool any = states.Values.Any(s => s != FileAssociationStatus.NotRegistered);
            if (initChecks)
            {
                foreach (string ext in _extensions)
                    _assocRows[ext].Box.IsChecked = !any || states[ext] != FileAssociationStatus.NotRegistered;
            }
            AssocStatusText.Text = string.Format(DialogText.Pref_Assoc_Summary, registered, _extensions.Count);
            AssocUnregisterButton.IsEnabled = any;
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            AssocStatusText.Text = string.Format(DialogText.Pref_StatusReadFailed, ex.Message);
            if (initChecks) foreach (var row in _assocRows.Values) row.Box.IsChecked = true;
            AssocUnregisterButton.IsEnabled = true;
        }
        UpdateAssocButtons();
    }

    private void OnAssocRegister(object sender, RoutedEventArgs e)
    {
        string? exe = SystemIntegration.ExePath;
        if (exe is null)
        {
            MessageDialog.Error(this, DialogText.Pref_Assoc_RegisterFailed_Title, DialogText.Sys_ExePathUnknown);
            return;
        }
        var selected = _extensions.Where(ext => _assocRows[ext].Box.IsChecked == true).ToList();
        if (selected.Count == 0) return;
        try
        {
            SystemIntegration.RegisterAssociations(exe, selected);
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            // Part of the keys may have been written: keep Unregister available.
            RefreshAssocStatus();
            AssocUnregisterButton.IsEnabled = true;
            MessageDialog.Error(this, DialogText.Pref_Assoc_RegisterFailed_Title, string.Format(DialogText.Pref_RegisterPartialBody, ex.Message));
            return;
        }
        RefreshAssocStatus();
        AssocStatusText.Text += " " + DialogText.Pref_Assoc_RegisterDone;
        Announce(AssocStatusText);
    }

    private void OnAssocUnregister(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemIntegration.UnregisterAssociations();
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            RefreshAssocStatus();
            MessageDialog.Error(this, DialogText.Pref_Assoc_UnregisterFailed_Title, ex.Message);
            return;
        }
        RefreshAssocStatus();
        AssocStatusText.Text += " " + DialogText.Pref_Assoc_UnregisterDone;
        Announce(AssocStatusText);
    }

    private void OnAssocOpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(FileAssociations.SettingsUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageDialog.Error(this, DialogText.Pref_OpenSettingsFailed_Title, ex.Message);
        }
    }

    private void RefreshShellStatus()
    {
        string? exe = SystemIntegration.ExePath;
        ShellRegisterButton.IsEnabled = exe is not null;
        if (exe is null)
        {
            ShellStatusText.Text = DialogText.Sys_ExePathUnknown;
            ShellUnregisterButton.IsEnabled = true;
            return;
        }
        try
        {
            var status = ShellMenu.GetStatus(Registry.CurrentUser, exe, _extensions);
            ShellStatusText.Text = status switch
            {
                ShellMenuStatus.Registered => DialogText.Pref_Shell_StatusRegistered,
                ShellMenuStatus.RegisteredElsewhere => DialogText.Pref_Shell_StatusElsewhere,
                _ => DialogText.Pref_Shell_StatusNotRegistered,
            };
            ShellUnregisterButton.IsEnabled = status != ShellMenuStatus.NotRegistered;
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            ShellStatusText.Text = string.Format(DialogText.Pref_StatusReadFailed, ex.Message);
            ShellUnregisterButton.IsEnabled = true;
        }
    }

    private void OnShellRegister(object sender, RoutedEventArgs e)
    {
        string? exe = SystemIntegration.ExePath;
        if (exe is null)
        {
            MessageDialog.Error(this, DialogText.Pref_Shell_RegisterFailed_Title, DialogText.Sys_ExePathUnknown);
            return;
        }
        // Consent: exactly which keys are written, for which program, with which items.
        string locations = string.Join("\n", _extensions.Select(ext => @"  HKCU\" + ShellMenu.MenuPath(ext)));
        string items = string.Join(", ", ShellMenu.Commands.Select(SystemIntegration.ShellLabel));
        int answer = MessageDialog.Show(this, DialogKind.Question, DialogText.Pref_Shell_Consent_Title,
            string.Format(DialogText.Pref_Shell_Consent_Body, locations, exe, items), null,
            [DialogText.Pref_Shell_Consent_Button, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        try
        {
            SystemIntegration.RegisterShellMenu(exe);
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            RefreshShellStatus();
            ShellUnregisterButton.IsEnabled = true;
            MessageDialog.Error(this, DialogText.Pref_Shell_RegisterFailed_Title, string.Format(DialogText.Pref_RegisterPartialBody, ex.Message));
            return;
        }
        RefreshShellStatus();
        ShellStatusText.Text += " " + DialogText.Pref_Shell_RegisterDone;
        Announce(ShellStatusText);
    }

    private void OnShellUnregister(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemIntegration.UnregisterShellMenu();
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            RefreshShellStatus();
            MessageDialog.Error(this, DialogText.Pref_Shell_UnregisterFailed_Title, ex.Message);
            return;
        }
        RefreshShellStatus();
        ShellStatusText.Text += " " + DialogText.Pref_Shell_UnregisterDone;
        Announce(ShellStatusText);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Store(_settings);
        DialogResult = true;
    }
}
