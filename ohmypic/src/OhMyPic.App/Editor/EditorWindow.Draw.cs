using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core.Editing;
using ShapePath = System.Windows.Shapes.Path;

namespace OhMyPic.App.Editor;

/// <summary>
/// Drawing tools (design P17): pen, rectangle and arrow in 6 colors and 3 widths, for marking up screenshots. A stroke is
/// previewed with WPF shapes in display coordinates and committed on mouse up as one <see cref="Annotation"/> in image
/// pixels (one undo step). Strokes are painted into the session's base image: the adjustments, rendered on top of the
/// base, apply to them as well, and crop/rotate treat them like the rest of the picture.
/// </summary>
public partial class EditorWindow
{
    /// <summary>Pen points closer than this (DIP) to the previous point are skipped.</summary>
    private const double PenStep = 1;

    /// <summary>Rectangles and arrows shorter than this (image pixels) count as a click and are dropped.</summary>
    private const double MinShapeLength = 2;

    // Arrow head as painted by Annotations.Apply: two strokes at ±30° from the end point, length Width × 4 (at least
    // 12 image pixels, at most the shaft). The preview uses the same numbers so it matches the result.
    private const double ArrowHeadFactor = 4;
    private const double ArrowHeadMin = 12;
    private const double ArrowHeadAngle = Math.PI / 6;

    private const int DefaultColorIndex = 0;
    private const int DefaultWidthIndex = 1;

    private static readonly IReadOnlyList<(AnnotationKind Kind, string Id, string Label)> DrawTools =
    [
        (AnnotationKind.Pen, "Pen", EditorText.Draw_Pen),
        (AnnotationKind.Rectangle, "Rectangle", EditorText.Draw_Rectangle),
        (AnnotationKind.Arrow, "Arrow", EditorText.Draw_Arrow),
    ];

    private static readonly IReadOnlyList<(string Id, string Label, Color Color)> DrawColors =
    [
        ("Red", EditorText.Draw_ColorRed, Color.FromRgb(0xE5, 0x39, 0x35)),
        ("Yellow", EditorText.Draw_ColorYellow, Color.FromRgb(0xFD, 0xD8, 0x35)),
        ("Green", EditorText.Draw_ColorGreen, Color.FromRgb(0x43, 0xA0, 0x47)),
        ("Blue", EditorText.Draw_ColorBlue, Color.FromRgb(0x1E, 0x88, 0xE5)),
        ("Black", EditorText.Draw_ColorBlack, Color.FromRgb(0x00, 0x00, 0x00)),
        ("White", EditorText.Draw_ColorWhite, Color.FromRgb(0xFF, 0xFF, 0xFF)),
    ];

    /// <summary>Stroke widths in image pixels.</summary>
    private static readonly IReadOnlyList<(string Id, string Label, double Width)> DrawWidths =
    [
        ("Thin", EditorText.Draw_WidthThin, 3),
        ("Medium", EditorText.Draw_WidthMedium, 6),
        ("Thick", EditorText.Draw_WidthThick, 12),
    ];

    private readonly List<(AnnotationKind Kind, ToggleButton Button)> _toolButtons = [];
    private readonly List<ToggleButton> _colorButtons = [];
    private readonly List<ToggleButton> _widthButtons = [];

    /// <summary>Points of the stroke being drawn, in image pixels.</summary>
    private readonly List<Point> _strokePoints = [];

    /// <summary>Active tool; null when drawing is off.</summary>
    private AnnotationKind? _tool;

    private int _colorIndex = DefaultColorIndex;
    private int _widthIndex = DefaultWidthIndex;
    private bool _stroking;

    /// <summary>True while <see cref="SyncDrawButtons"/> sets the toggles, so their Checked/Unchecked handlers do not react.</summary>
    private bool _syncingDraw;
    private Point _strokeStart;
    private Point _strokeLast;
    private Shape? _preview;

    private void InitDraw()
    {
        var style = (Style)FindResource("Ed.PanelToggle");
        foreach (var (kind, id, label) in DrawTools)
        {
            var button = new ToggleButton { Content = label, Style = style };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "OP-Editor-Draw-" + id);
            // Checked/Unchecked rather than Click: mouse, keyboard and UIA Toggle all change the state (a Click handler
            // misses UIA Toggle). Unchecking the active tool turns drawing off.
            button.Checked += (_, _) => { if (!_syncingDraw) SelectTool(kind); };
            button.Unchecked += (_, _) => { if (!_syncingDraw && _tool == kind) SelectTool(null); };
            DrawToolPanel.Children.Add(button);
            _toolButtons.Add((kind, button));
        }
        for (int i = 0; i < DrawColors.Count; i++)
        {
            var (id, label, color) = DrawColors[i];
            var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
            swatch.Background = new SolidColorBrush(color);
            swatch.SetResourceReference(Border.BorderBrushProperty, ThemeManager.Border);
            var button = new ToggleButton { Content = swatch, Style = style, Padding = new Thickness(6), ToolTip = label };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "OP-Editor-Draw-Color-" + id);
            int index = i;
            button.Checked += (_, _) => { if (!_syncingDraw) SelectColor(index); };
            // One color is always chosen: unchecking the current swatch checks it again.
            button.Unchecked += (_, _) => { if (!_syncingDraw) SyncDrawButtons(); };
            DrawColorPanel.Children.Add(button);
            _colorButtons.Add(button);
        }
        for (int i = 0; i < DrawWidths.Count; i++)
        {
            var (id, label, _) = DrawWidths[i];
            var button = new ToggleButton { Content = label, Style = style };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "OP-Editor-Draw-Width-" + id);
            int index = i;
            button.Checked += (_, _) => { if (!_syncingDraw) SelectWidth(index); };
            button.Unchecked += (_, _) => { if (!_syncingDraw) SyncDrawButtons(); };
            DrawWidthPanel.Children.Add(button);
            _widthButtons.Add(button);
        }

        DrawLayer.MouseLeftButtonDown += OnDrawMouseDown;
        DrawLayer.MouseMove += OnDrawMouseMove;
        DrawLayer.MouseLeftButtonUp += OnDrawMouseUp;
        DrawLayer.LostMouseCapture += (_, _) => CancelStroke();
        // Drawing starts off; capture mode turns the Pen on once its opening crop is applied or cancelled.
        SyncDrawButtons();
    }

    /// <summary>Turns a tool on (cancelling an active crop) or drawing off (null).</summary>
    private void SelectTool(AnnotationKind? tool)
    {
        CancelStroke();
        if (tool is not null && _cropping) EndCrop();
        _tool = tool;
        DrawLayer.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        SyncDrawButtons();
    }

    private void SelectColor(int index)
    {
        _colorIndex = index;
        SyncDrawButtons();
    }

    private void SelectWidth(int index)
    {
        _widthIndex = index;
        SyncDrawButtons();
    }

    /// <summary>Checked state of every drawing toggle and the draw layer's help text from the current choice.</summary>
    private void SyncDrawButtons()
    {
        _syncingDraw = true;
        try
        {
            foreach (var (kind, button) in _toolButtons) button.IsChecked = kind == _tool;
            for (int i = 0; i < _colorButtons.Count; i++) _colorButtons[i].IsChecked = i == _colorIndex;
            for (int i = 0; i < _widthButtons.Count; i++) _widthButtons[i].IsChecked = i == _widthIndex;
        }
        finally
        {
            _syncingDraw = false;
        }
        if (_tool is { } tool)
        {
            string toolLabel = DrawTools.First(t => t.Kind == tool).Label;
            AutomationProperties.SetHelpText(DrawLayer,
                string.Format(EditorText.Draw_LayerHelp, toolLabel, DrawColors[_colorIndex].Label, DrawWidths[_widthIndex].Label));
        }
    }

    // ───────────── pointer ─────────────

    private Point ToImage(Point display) => new(display.X / _zoom, display.Y / _zoom);

    private void OnDrawMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool is not { } tool || _busy || _cropping || _stroking) return;
        e.Handled = true;
        var p = e.GetPosition(DrawLayer);
        if (!DrawLayer.CaptureMouse()) return;
        _stroking = true;
        _strokeStart = p;
        _strokeLast = p;
        _strokePoints.Clear();
        _strokePoints.Add(ToImage(p));
        _preview = CreatePreview(tool);
        DrawLayer.Children.Add(_preview);
        UpdatePreview(p);
    }

    private void OnDrawMouseMove(object sender, MouseEventArgs e)
    {
        if (!_stroking) return;
        var p = e.GetPosition(DrawLayer);
        if (_tool == AnnotationKind.Pen)
        {
            if ((p - _strokeLast).Length < PenStep) return;
            _strokePoints.Add(ToImage(p));
        }
        _strokeLast = p;
        UpdatePreview(p);
    }

    private void OnDrawMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stroking || _tool is not { } tool) return;
        e.Handled = true;
        var p = e.GetPosition(DrawLayer);
        Point[] points;
        if (tool == AnnotationKind.Pen)
        {
            if ((p - _strokeLast).Length >= PenStep) _strokePoints.Add(ToImage(p));
            // One point (no movement) is a dot.
            points = [.. _strokePoints];
        }
        else
        {
            points = [ToImage(_strokeStart), ToImage(p)];
        }
        CancelStroke();
        if (tool != AnnotationKind.Pen && (points[1] - points[0]).Length < MinShapeLength) return;
        CommitStroke(new Annotation(tool, points, DrawColors[_colorIndex].Color, DrawWidths[_widthIndex].Width));
    }

    /// <summary>Drops the stroke being drawn (Esc, lost mouse capture, zoom or layout change). No-op when none.</summary>
    private void CancelStroke()
    {
        if (!_stroking) return;
        _stroking = false;
        _strokePoints.Clear();
        if (_preview is not null) DrawLayer.Children.Remove(_preview);
        _preview = null;
        // Raises LostMouseCapture, which finds no stroke any more.
        if (DrawLayer.IsMouseCaptured) DrawLayer.ReleaseMouseCapture();
    }

    private async void CommitStroke(Annotation annotation)
    {
        try
        {
            await EditAsync(s => s.Draw(annotation));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(false);
            MessageDialog.Error(this, MainText.Error_CommandFailedTitle, string.Format(MainText.Error_CommandFailedBody, ex.Message));
            SyncFromSession();
        }
    }

    // ───────────── preview ─────────────

    /// <summary>Stroke shape in the chosen color, width scaled to the zoom, round caps and joins like the painted result.</summary>
    private Shape CreatePreview(AnnotationKind tool)
    {
        var brush = new SolidColorBrush(DrawColors[_colorIndex].Color);
        brush.Freeze();
        Shape shape = tool == AnnotationKind.Pen ? new Polyline() : new ShapePath();
        shape.Stroke = brush;
        shape.StrokeThickness = DrawWidths[_widthIndex].Width * _zoom;
        shape.StrokeStartLineCap = PenLineCap.Round;
        shape.StrokeEndLineCap = PenLineCap.Round;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.IsHitTestVisible = false;
        return shape;
    }

    private void UpdatePreview(Point p)
    {
        switch (_preview)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case ShapePath path when _tool == AnnotationKind.Rectangle:
                path.Data = new RectangleGeometry(new Rect(_strokeStart, p));
                break;
            case ShapePath path:
                path.Data = ArrowGeometry(_strokeStart, p);
                break;
        }
    }

    /// <summary>Shaft from start to end plus the two head strokes, in display coordinates.</summary>
    private Geometry ArrowGeometry(Point start, Point end)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.LineTo(end, true, true);
            var shaft = end - start;
            double length = shaft.Length;
            if (length > 0)
            {
                double head = Math.Min(Math.Max(DrawWidths[_widthIndex].Width * ArrowHeadFactor, ArrowHeadMin) * _zoom, length);
                var back = -shaft / length;
                foreach (double angle in new[] { ArrowHeadAngle, -ArrowHeadAngle })
                {
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    var wing = new Vector(back.X * cos - back.Y * sin, back.X * sin + back.Y * cos);
                    context.BeginFigure(end, false, false);
                    context.LineTo(end + wing * head, true, true);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
