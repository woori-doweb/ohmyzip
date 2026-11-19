using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OhMyPic.App.Text;
using OhMyPic.Core.Editing;

namespace OhMyPic.App.Editor;

/// <summary>Inspector adjustments (9 sliders + Reset) and presets (8 buttons with thumbnails).</summary>
public partial class EditorWindow
{
    /// <summary>Preset thumbnail long side in DIP.</summary>
    private const int PresetThumbSide = 64;

    private sealed record SliderSpec(
        string Id,
        string Label,
        int Min,
        Func<AdjustmentSettings, int> Get,
        Func<AdjustmentSettings, int, AdjustmentSettings> Set);

    private static readonly IReadOnlyList<SliderSpec> SliderSpecs =
    [
        new("Brightness", EditorText.Adjust_Brightness, AdjustmentSettings.SignedMin, s => s.Brightness, (s, v) => s with { Brightness = v }),
        new("Contrast", EditorText.Adjust_Contrast, AdjustmentSettings.SignedMin, s => s.Contrast, (s, v) => s with { Contrast = v }),
        new("Saturation", EditorText.Adjust_Saturation, AdjustmentSettings.SignedMin, s => s.Saturation, (s, v) => s with { Saturation = v }),
        new("Warmth", EditorText.Adjust_Warmth, AdjustmentSettings.SignedMin, s => s.Warmth, (s, v) => s with { Warmth = v }),
        new("Hue", EditorText.Adjust_Hue, AdjustmentSettings.SignedMin, s => s.Hue, (s, v) => s with { Hue = v }),
        new("Sharpen", EditorText.Adjust_Sharpen, 0, s => s.Sharpen, (s, v) => s with { Sharpen = v }),
        new("Vignette", EditorText.Adjust_Vignette, 0, s => s.Vignette, (s, v) => s with { Vignette = v }),
        new("Grayscale", EditorText.Adjust_Grayscale, 0, s => s.Grayscale, (s, v) => s with { Grayscale = v }),
        new("Sepia", EditorText.Adjust_Sepia, 0, s => s.Sepia, (s, v) => s with { Sepia = v }),
    ];

    private readonly List<(SliderSpec Spec, Slider Slider, TextBlock Value)> _sliders = [];
    private readonly List<Image> _presetImages = [];

    /// <summary>True while sliders are set from the session (no live change, no commit).</summary>
    private bool _syncing;

    /// <summary>True while a slider thumb is dragged: the change is committed once, on drag completion.</summary>
    private bool _dragging;

    private int _thumbVersion;

    private void BuildAdjustPanel()
    {
        foreach (var spec in SliderSpecs)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 1);
            AutomationProperties.SetHelpText(value, string.Format(EditorText.Adjust_ValueName, spec.Label));
            AutomationProperties.SetAutomationId(value, "OP-Editor-Value-" + spec.Id);

            var slider = new Slider { Minimum = spec.Min, Maximum = AdjustmentSettings.Max, Value = 0 };
            Grid.SetRow(slider, 1);
            Grid.SetColumnSpan(slider, 2);
            AutomationProperties.SetName(slider, spec.Label);
            AutomationProperties.SetAutomationId(slider, "OP-Editor-Slider-" + spec.Id);
            slider.ValueChanged += (_, e) => OnSliderChanged(spec, value, e.NewValue);
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
            {
                // A keyboard change still waiting to commit becomes part of this drag's single step.
                _commitTimer.Stop();
                _dragging = true;
            }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            {
                _dragging = false;
                CommitAdjustments();
            }));

            row.Children.Add(label);
            row.Children.Add(value);
            row.Children.Add(slider);
            AdjustPanel.Children.Add(row);
            _sliders.Add((spec, slider, value));
            value.Text = FormatValue(spec, 0);
        }
    }

    private static string FormatValue(SliderSpec spec, int value) =>
        spec.Min < 0 && value > 0 ? "+" + value.ToString(CultureInfo.CurrentCulture) : value.ToString(CultureInfo.CurrentCulture);

    private void OnSliderChanged(SliderSpec spec, TextBlock valueText, double newValue)
    {
        int value = (int)Math.Round(newValue);
        valueText.Text = FormatValue(spec, value);
        if (_syncing) return;
        _live = spec.Set(_live, value);
        RequestRender();
        if (!_dragging)
        {
            _commitTimer.Stop();
            _commitTimer.Start();
        }
        UpdateState();
    }

    private void SetSliders(AdjustmentSettings settings)
    {
        _syncing = true;
        try
        {
            foreach (var (spec, slider, value) in _sliders)
            {
                int v = spec.Get(settings);
                slider.Value = v;
                value.Text = FormatValue(spec, v);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Records the live slider values as one undo step (no step when nothing changed).</summary>
    private void CommitAdjustments()
    {
        _commitTimer.Stop();
        if (_closed || _live == _session.Settings) return;
        _session.SetAdjustments(_live);
        ReportDroppedHistory();
        UpdateState();
    }

    /// <summary>Reset and presets: replaces all adjustment values in one undo step.</summary>
    private void ApplyAdjustments(AdjustmentSettings settings)
    {
        if (_busy || _closed) return;
        CommitAdjustments();
        _session.SetAdjustments(settings);
        SyncFromSession();
    }

    private void OnResetAdjustments(object sender, RoutedEventArgs e) => ApplyAdjustments(AdjustmentSettings.Identity);

    // ───────────── presets ─────────────

    private void BuildPresetPanel()
    {
        var style = (Style)FindResource("Ed.PanelButton");
        foreach (var (key, settings) in Adjustments.Presets)
        {
            string label = PresetLabel(key);
            var image = new Image { Width = PresetThumbSide, Height = PresetThumbSide, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var content = new StackPanel();
            content.Children.Add(image);
            content.Children.Add(new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var button = new Button { Content = content, Style = style, Width = 84, Padding = new Thickness(4) };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "OP-Editor-Preset-" + key);
            var preset = settings;
            button.Click += (_, _) => ApplyAdjustments(preset);
            PresetPanel.Children.Add(button);
            _presetImages.Add(image);
        }
    }

    private static string PresetLabel(string key) => key switch
    {
        "original" => EditorText.Preset_Original,
        "vivid" => EditorText.Preset_Vivid,
        "warm" => EditorText.Preset_Warm,
        "cool" => EditorText.Preset_Cool,
        "faded" => EditorText.Preset_Faded,
        "dramatic" => EditorText.Preset_Dramatic,
        "soft" => EditorText.Preset_Soft,
        "mono" => EditorText.Preset_Mono,
        _ => key,
    };

    /// <summary>Renders the preset thumbnails from the current base image on a background task (newest base wins).</summary>
    private async void RefreshPresetThumbnails()
    {
        int version = ++_thumbVersion;
        var source = _session.Base;
        int side = Math.Max(1, (int)Math.Ceiling(PresetThumbSide * VisualTreeHelper.GetDpi(this).DpiScaleX));
        try
        {
            var bitmaps = await Task.Run(() =>
            {
                var small = Adjustments.ApplyPreview(source, AdjustmentSettings.Identity, side);
                return Adjustments.Presets.Select(p => Adjustments.ApplyPreview(small, p.Settings, side).ToBitmap()).ToList();
            });
            if (version != _thumbVersion || _closed) return;
            for (int i = 0; i < bitmaps.Count && i < _presetImages.Count; i++)
                _presetImages[i].Source = bitmaps[i];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowNotice(string.Format(EditorText.Status_PreviewFailed, ex.Message));
        }
    }
}
