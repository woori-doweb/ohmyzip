using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core.Editing;

namespace OhMyPic.App.Editor;

/// <summary>
/// Crop mode: an aspect choice and a frame over the canvas. Inside the frame drags move it; edges and corners resize it
/// (fixed aspects keep the ratio, anchored at the opposite side); outside starts a new frame. Every rectangle is snapped
/// with <see cref="Crop.Constrain"/>, so the applied size is exact.
/// </summary>
public partial class EditorWindow
{
    /// <summary>Pointer reach of edges and corners in DIP on each side (32-DIP targets).</summary>
    private const double HandleReach = 16;

    private const double HandleSize = 10;

    private enum CropHandle { None, Move, N, S, E, W, NE, NW, SE, SW }

    private readonly List<Rectangle> _handles = [];
    private bool _cropping;
    private bool _aspectReady;
    private Int32Rect _cropRect;
    private CropHandle _drag;
    private Point _dragStart;
    private Int32Rect _dragStartRect;

    private void InitCrop()
    {
        foreach (var aspect in Enum.GetValues<CropAspect>())
        {
            var item = new ComboBoxItem { Content = AspectLabel(aspect), Tag = aspect, MinHeight = 32 };
            AutomationProperties.SetAutomationId(item, "OP-Editor-Crop-Aspect-" + aspect);
            AspectBox.Items.Add(item);
        }
        AspectBox.SelectedIndex = 0;
        _aspectReady = true;

        for (int i = 0; i < 8; i++)
        {
            var handle = new Rectangle { Width = HandleSize, Height = HandleSize, StrokeThickness = 1, IsHitTestVisible = false };
            handle.SetResourceReference(Shape.FillProperty, ThemeManager.Accent);
            handle.SetResourceReference(Shape.StrokeProperty, ThemeManager.Background);
            _handles.Add(handle);
            CropLayer.Children.Add(handle);
        }
        CropLayer.MouseLeftButtonDown += OnCropMouseDown;
        CropLayer.MouseMove += OnCropMouseMove;
        CropLayer.MouseLeftButtonUp += OnCropMouseUp;
        CropLayer.LostMouseCapture += (_, _) => _drag = CropHandle.None;
    }

    private static string AspectLabel(CropAspect aspect)
    {
        if (aspect == CropAspect.Free) return EditorText.Crop_AspectFree;
        var (w, h) = Crop.Ratio(aspect);
        return $"{w}:{h}";
    }

    private CropAspect CurrentAspect => AspectBox.SelectedItem is ComboBoxItem { Tag: CropAspect aspect } ? aspect : CropAspect.Free;

    private void OnAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_aspectReady) return;
        if (!_cropping)
        {
            // Choosing an aspect starts cropping with the largest centred frame of that aspect.
            if (CanRun("Crop")) Run("Crop");
            return;
        }
        var b = _session.Base;
        _cropRect = Crop.FitAspect(b.Width, b.Height, CurrentAspect);
        UpdateCropVisuals();
    }

    private void StartCrop()
    {
        if (_cropping) return;
        var b = _session.Base;
        _cropping = true;
        _cropRect = Crop.FitAspect(b.Width, b.Height, CurrentAspect);
        CropLayer.Visibility = Visibility.Visible;
        UpdateCropVisuals();
        UpdateState();
    }

    private void EndCrop()
    {
        _cropping = false;
        _drag = CropHandle.None;
        if (CropLayer.IsMouseCaptured) CropLayer.ReleaseMouseCapture();
        CropLayer.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    private async Task ApplyCropAsync()
    {
        if (!_cropping) return;
        var rect = _cropRect;
        var b = _session.Base;
        EndCrop();
        if (rect.X == 0 && rect.Y == 0 && rect.Width == b.Width && rect.Height == b.Height) return;
        await EditAsync(s => s.Crop(rect));
    }

    private void OnCropApply(object sender, RoutedEventArgs e) => Run("CropApply");

    private void OnCropCancel(object sender, RoutedEventArgs e) => Run("CropCancel");

    // ───────────── frame drawing ─────────────

    private Rect DisplayRect() => new(_cropRect.X * _zoom, _cropRect.Y * _zoom, _cropRect.Width * _zoom, _cropRect.Height * _zoom);

    private void UpdateCropVisuals()
    {
        if (!_cropping || double.IsNaN(CropLayer.Width)) return;
        var r = DisplayRect();
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(new Rect(0, 0, CropLayer.Width, CropLayer.Height)));
        shade.Children.Add(new RectangleGeometry(r));
        CropShade.Data = shade;
        Canvas.SetLeft(CropFrame, r.X);
        Canvas.SetTop(CropFrame, r.Y);
        CropFrame.Width = r.Width;
        CropFrame.Height = r.Height;

        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        Point[] points =
        [
            new(r.Left, r.Top), new(cx, r.Top), new(r.Right, r.Top), new(r.Right, cy),
            new(r.Right, r.Bottom), new(cx, r.Bottom), new(r.Left, r.Bottom), new(r.Left, cy),
        ];
        for (int i = 0; i < _handles.Count; i++)
        {
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }
        CropSizeText.Text = string.Format(EditorText.Size_Pixels, _cropRect.Width, _cropRect.Height);
        AutomationProperties.SetHelpText(CropLayer, string.Format(EditorText.Crop_FrameHelp, _cropRect.Width, _cropRect.Height));
    }

    // ───────────── pointer ─────────────

    private CropHandle HitTest(Point p)
    {
        var r = DisplayRect();
        bool left = Math.Abs(p.X - r.Left) <= HandleReach, right = Math.Abs(p.X - r.Right) <= HandleReach;
        bool top = Math.Abs(p.Y - r.Top) <= HandleReach, bottom = Math.Abs(p.Y - r.Bottom) <= HandleReach;
        bool inX = p.X >= r.Left - HandleReach && p.X <= r.Right + HandleReach;
        bool inY = p.Y >= r.Top - HandleReach && p.Y <= r.Bottom + HandleReach;
        if (top && left) return CropHandle.NW;
        if (top && right) return CropHandle.NE;
        if (bottom && left) return CropHandle.SW;
        if (bottom && right) return CropHandle.SE;
        if (top && inX) return CropHandle.N;
        if (bottom && inX) return CropHandle.S;
        if (left && inY) return CropHandle.W;
        if (right && inY) return CropHandle.E;
        return r.Contains(p) ? CropHandle.Move : CropHandle.None;
    }

    private static Cursor CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.Move => Cursors.SizeAll,
        CropHandle.N or CropHandle.S => Cursors.SizeNS,
        CropHandle.E or CropHandle.W => Cursors.SizeWE,
        CropHandle.NW or CropHandle.SE => Cursors.SizeNWSE,
        CropHandle.NE or CropHandle.SW => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_cropping || _busy) return;
        var p = e.GetPosition(CropLayer);
        _drag = HitTest(p);
        _dragStart = p;
        _dragStartRect = _cropRect;
        if (_drag == CropHandle.None)
        {
            // Outside the frame: a new frame grows from the pointer.
            var b = _session.Base;
            int x = Math.Clamp((int)Math.Floor(p.X / _zoom), 0, b.Width - 1);
            int y = Math.Clamp((int)Math.Floor(p.Y / _zoom), 0, b.Height - 1);
            _dragStartRect = new Int32Rect(x, y, 0, 0);
            _drag = CropHandle.SE;
        }
        CropLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropping) return;
        var p = e.GetPosition(CropLayer);
        if (_drag == CropHandle.None)
        {
            CropLayer.Cursor = CursorFor(HitTest(p));
            return;
        }
        _cropRect = DragRect(p);
        UpdateCropVisuals();
    }

    private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        _drag = CropHandle.None;
        if (CropLayer.IsMouseCaptured) CropLayer.ReleaseMouseCapture();
    }

    /// <summary>Frame for the current drag, in image pixels, snapped by <see cref="Crop.Constrain"/>.</summary>
    private Int32Rect DragRect(Point p)
    {
        var b = _session.Base;
        int width = b.Width, height = b.Height;
        double dx = (p.X - _dragStart.X) / _zoom, dy = (p.Y - _dragStart.Y) / _zoom;
        var s = _dragStartRect;
        if (_drag == CropHandle.Move)
        {
            int x = (int)Math.Round(Math.Clamp(s.X + dx, 0, width - s.Width));
            int y = (int)Math.Round(Math.Clamp(s.Y + dy, 0, height - s.Height));
            return new Int32Rect(x, y, s.Width, s.Height);
        }

        bool west = _drag is CropHandle.W or CropHandle.NW or CropHandle.SW;
        bool east = _drag is CropHandle.E or CropHandle.NE or CropHandle.SE;
        bool north = _drag is CropHandle.N or CropHandle.NW or CropHandle.NE;
        bool south = _drag is CropHandle.S or CropHandle.SW or CropHandle.SE;
        var aspect = CurrentAspect;
        double left, top, w, h;
        if (aspect == CropAspect.Free)
        {
            double l = s.X, t = s.Y, r = s.X + s.Width, bt = s.Y + s.Height;
            if (west) l += dx;
            if (east) r += dx;
            if (north) t += dy;
            if (south) bt += dy;
            l = Math.Clamp(l, 0, width);
            r = Math.Clamp(r, 0, width);
            t = Math.Clamp(t, 0, height);
            bt = Math.Clamp(bt, 0, height);
            (left, w) = (Math.Min(l, r), Math.Abs(r - l));
            (top, h) = (Math.Min(t, bt), Math.Abs(bt - t));
        }
        else
        {
            (left, top, w, h) = AspectDrag(Crop.Ratio(aspect), s, dx, dy, west, east, north, south, width, height);
        }
        var requested = new Int32Rect(
            (int)Math.Floor(left), (int)Math.Floor(top), Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
        return Crop.Constrain(requested, aspect, width, height);
    }

    /// <summary>
    /// Fixed-aspect resize: the side(s) opposite the dragged handle stay put (an edge drag keeps the other axis centred),
    /// the size follows the pointer, and the frame shrinks to fit the image.
    /// </summary>
    private static (double Left, double Top, double Width, double Height) AspectDrag(
        (int Width, int Height) ratio, Int32Rect s, double dx, double dy, bool west, bool east, bool north, bool south, int width, int height)
    {
        double a = ratio.Width / (double)ratio.Height;
        bool horizontal = west || east, vertical = north || south;
        double ax = west ? s.X + s.Width : s.X, ay = north ? s.Y + s.Height : s.Y;
        double cx = s.X + s.Width / 2.0, cy = s.Y + s.Height / 2.0;
        double mx = west ? s.X + dx : s.X + s.Width + dx, my = north ? s.Y + dy : s.Y + s.Height + dy;
        double w = 0, h = 0;
        int sx = 1, sy = 1;
        if (horizontal)
        {
            w = Math.Abs(mx - ax);
            sx = mx >= ax ? 1 : -1;
        }
        if (vertical)
        {
            h = Math.Abs(my - ay);
            sy = my >= ay ? 1 : -1;
        }
        if (horizontal && vertical)
        {
            if (w >= h * a) h = w / a;
            else w = h * a;
        }
        else if (horizontal) h = w / a;
        else w = h * a;
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        double maxW = horizontal ? (sx > 0 ? width - ax : ax) : 2 * Math.Min(cx, width - cx);
        double maxH = vertical ? (sy > 0 ? height - ay : ay) : 2 * Math.Min(cy, height - cy);
        double k = Math.Min(1, Math.Min(Math.Max(0, maxW) / w, Math.Max(0, maxH) / h));
        w *= k;
        h *= k;
        double left = horizontal ? (sx > 0 ? ax : ax - w) : cx - w / 2;
        double top = vertical ? (sy > 0 ? ay : ay - h) : cy - h / 2;
        return (left, top, w, h);
    }
}
