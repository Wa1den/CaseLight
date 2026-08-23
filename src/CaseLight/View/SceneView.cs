using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CaseLight.Model;

namespace CaseLight.View;

/// <summary>
/// The scene: the monitor and every fixture, drawn to scale in millimetres.
///
/// Drawn by hand rather than assembled from WPF shapes because the interesting part is the
/// individual LEDs - a hundred and some dots that move whenever a rectangle is dragged,
/// resized or turned. Retained shapes for those would cost more than they are worth.
/// </summary>
public sealed class SceneView : FrameworkElement
{
    public Scene Scene { get; set; } = new();
    public Fixture? Selected { get; private set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? FixtureChanged;

    /// <summary>Pixels per millimetre.</summary>
    double _scale = 0.6;
    Point _origin = new(40, 40);

    // ---- тест размещения --------------------------------------------------

    /// <summary>
    /// A movable patch of light that stands in for the screen.
    ///
    /// Checking a layout against real content is guesswork - the picture changes faster
    /// than you can look at the case. A single spot you drag by hand answers the only
    /// question that matters: does the thing that lights up correspond to where the spot is.
    /// </summary>
    public bool TestMode { get; set; }
    public Point TestCenter { get; set; }
    public double TestSizeMm { get; set; } = 150;
    public TestShape TestShape { get; set; } = TestShape.Circle;
    public Color TestColor { get; set; } = Colors.OrangeRed;

    public event EventHandler? TestMoved;

    enum Drag { None, Move, Resize, Rotate, Pan, TestPatch }
    Drag _drag = Drag.None;
    int _resizeCorner;
    Point _testStart;
    Point _dragStart;
    Fixture _before = new();
    Point _originStart;

    const double HandleRadius = 6;

    static readonly Typeface Font = new("Segoe UI");

    public SceneView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // ---- координаты -------------------------------------------------------

    Point ToScreen(Point mm) => new(_origin.X + mm.X * _scale, _origin.Y + mm.Y * _scale);
    Point ToScene(Point px) => new((px.X - _origin.X) / _scale, (px.Y - _origin.Y) / _scale);

    /// <summary>Frames everything with a margin, so a freshly loaded layout is simply visible.</summary>
    public void FitToContent()
    {
        double minX = Scene.Monitor.CenterX - Scene.Monitor.Width / 2;
        double maxX = Scene.Monitor.CenterX + Scene.Monitor.Width / 2;
        double minY = Scene.Monitor.CenterY - Scene.Monitor.Height / 2;
        double maxY = Scene.Monitor.CenterY + Scene.Monitor.Height / 2;

        foreach (var f in Scene.Fixtures)
            foreach (var c in LedGeometry.Corners(f))
            {
                minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
                minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
            }

        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        if (ActualWidth < 10 || ActualHeight < 10) return;

        _scale = Math.Min((ActualWidth - 80) / w, (ActualHeight - 80) / h);
        _scale = Math.Clamp(_scale, 0.05, 20);

        _origin = new Point(ActualWidth / 2 - (minX + maxX) / 2 * _scale,
                            ActualHeight / 2 - (minY + maxY) / 2 * _scale);
        InvalidateVisual();
    }

    public void Select(Fixture? f)
    {
        Selected = f;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- цвета ------------------------------------------------------------

    /// <summary>
    /// The canvas is drawn by hand, so it has to fetch the theme colours itself instead of
    /// inheriting them from a control template. The aliases come from App.xaml and follow
    /// the system light/dark switch; a redraw picks up whatever they hold at that moment.
    /// </summary>
    static Brush Themed(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    static Color ThemedColor(string key) =>
        Themed(key) is SolidColorBrush b ? b.Color : Colors.Gray;

    /// <summary>The foreground colour at a given transparency, for fills and faint outlines.</summary>
    static Brush Ink(double opacity)
    {
        var c = ThemedColor("Fg");
        return new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B));
    }

    // ---- отрисовка --------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Themed("Panel"), null,
                         new Rect(0, 0, ActualWidth, ActualHeight));

        DrawGrid(dc);
        DrawMonitor(dc);

        foreach (var f in Scene.Fixtures)
            DrawFixture(dc, f, f == Selected);

        if (Selected != null && !TestMode) DrawHandles(dc, Selected);
        if (TestMode) DrawTestPatch(dc);
    }

    void DrawTestPatch(DrawingContext dc)
    {
        var centre = ToScreen(TestCenter);
        double halfPx = TestSizeMm / 2 * _scale;

        var fill = new SolidColorBrush(Color.FromArgb(150, TestColor.R, TestColor.G, TestColor.B));
        var pen = new Pen(Themed("Fg"), 1.5);

        if (TestShape == TestShape.Circle)
            dc.DrawEllipse(fill, pen, centre, halfPx, halfPx);
        else
            dc.DrawRectangle(fill, pen, new Rect(centre.X - halfPx, centre.Y - halfPx, halfPx * 2, halfPx * 2));

        Label(dc, "тестовое пятно, перемещается мышью", new Point(centre.X - halfPx, centre.Y - halfPx - 18),
              ThemedColor("Fg"), 12);
    }

    bool HitTestPatch(Point px)
    {
        var centre = ToScreen(TestCenter);
        double halfPx = TestSizeMm / 2 * _scale;

        return TestShape == TestShape.Circle
            ? Distance(px, centre) <= halfPx
            : Math.Abs(px.X - centre.X) <= halfPx && Math.Abs(px.Y - centre.Y) <= halfPx;
    }

    void DrawGrid(DrawingContext dc)
    {
        var pen = new Pen(Themed("PanelStroke"), 1);

        // 100 mm grid, dropped once it would turn into mush
        double stepPx = 100 * _scale;
        if (stepPx < 12) return;

        double x0 = _origin.X % stepPx, y0 = _origin.Y % stepPx;
        for (double x = x0; x < ActualWidth; x += stepPx) dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        for (double y = y0; y < ActualHeight; y += stepPx) dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
    }

    void DrawMonitor(DrawingContext dc)
    {
        var m = Scene.Monitor;
        var tl = ToScreen(new Point(m.CenterX - m.Width / 2, m.CenterY - m.Height / 2));
        var br = ToScreen(new Point(m.CenterX + m.Width / 2, m.CenterY + m.Height / 2));
        var rect = new Rect(tl, br);

        // No caption: the screen is the only rectangle of its kind on the canvas, and one
        // more word among the fixture labels is one more thing to read past.
        dc.DrawRectangle(Ink(0.05), new Pen(Ink(0.35), 2), rect);
    }

    void DrawFixture(DrawingContext dc, Fixture f, bool selected)
    {
        var corners = LedGeometry.Corners(f).Select(ToScreen).ToArray();

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(corners[0], true, true);
            ctx.PolyLineTo(new[] { corners[1], corners[2], corners[3] }, true, true);
        }
        geometry.Freeze();

        var tint = ParseTint(f.Tint);
        var fill = new SolidColorBrush(Color.FromArgb((byte)(selected ? 46 : 24), tint.R, tint.G, tint.B));
        var pen = new Pen(selected ? Themed("Fg") : new SolidColorBrush(tint), selected ? 2 : 1);

        dc.DrawGeometry(fill, pen, geometry);

        // the LEDs themselves - the whole reason this is drawn by hand
        var leds = LedGeometry.World(f);
        double dot = Math.Clamp(2.2 * _scale, 1.6, 5.0);

        for (int i = 0; i < leds.Length; i++)
        {
            var p = ToScreen(leds[i]);

            // The anchor and its immediate neighbours are marked: together they show both
            // where the run starts and which way round it goes - the two things that are
            // impossible to guess and easy to get backwards.
            Brush brush = i == f.AnchorLed
                ? Brushes.OrangeRed
                : IsNear(f, i, 3) ? Brushes.Orange
                : new SolidColorBrush(tint);

            dc.DrawEllipse(brush, null, p, i == f.AnchorLed ? dot * 1.7 : dot, i == f.AnchorLed ? dot * 1.7 : dot);
        }

        // Just the name: the LED count is in the fixture panel and on the list, and on a
        // canvas of eight labels every extra word is one more thing to read past.
        var top = corners.OrderBy(c => c.Y).First();
        Label(dc, f.Name, new Point(top.X - 20, top.Y - 20),
              ThemedColor(selected ? "Fg" : "FgDim"), 12);
    }

    /// <summary>True for the few LEDs just after the anchor, walked the way the fixture is walked.</summary>
    static bool IsNear(Fixture f, int i, int within)
    {
        int n = f.LedCount;
        if (n == 0) return false;

        int step = i - f.AnchorLed;
        if (f.Reverse) step = -step;
        int k = ((step % n) + n) % n;
        return k > 0 && k <= within;
    }

    void DrawHandles(DrawingContext dc, Fixture f)
    {
        var corners = LedGeometry.Corners(f).Select(ToScreen).ToArray();
        var pen = new Pen(Themed("Fg"), 1.5);

        foreach (var c in corners)
            dc.DrawRectangle(Themed("Fg"), pen,
                             new Rect(c.X - HandleRadius / 2, c.Y - HandleRadius / 2, HandleRadius, HandleRadius));

        var handle = RotateHandle(f);
        var mid = new Point((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        dc.DrawLine(pen, mid, handle);
        dc.DrawEllipse(Brushes.Gold, pen, handle, HandleRadius, HandleRadius);
    }

    Point RotateHandle(Fixture f)
    {
        var corners = LedGeometry.Corners(f).Select(ToScreen).ToArray();
        var mid = new Point((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        var centre = ToScreen(new Point(f.CenterX, f.CenterY));

        var dx = mid.X - centre.X;
        var dy = mid.Y - centre.Y;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

        return new Point(mid.X + dx / len * 28, mid.Y + dy / len * 28);
    }

    void Label(DrawingContext dc, string text, Point at, Color colour, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   Font, size, new SolidColorBrush(colour),
                                   VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    static Color ParseTint(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.CornflowerBlue; }
    }

    // ---- мышь -------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var px = e.GetPosition(this);

        // While testing, the patch is the only thing worth grabbing - moving fixtures at
        // the same time would just make it impossible to tell what changed.
        if (TestMode)
        {
            if (HitTestPatch(px)) { _drag = Drag.TestPatch; _dragStart = px; _testStart = TestCenter; CaptureMouse(); }
            return;
        }

        if (Selected != null)
        {
            if (Distance(px, RotateHandle(Selected)) <= HandleRadius + 3)
            {
                Begin(Drag.Rotate, px);
                CaptureMouse();
                return;
            }

            var corners = LedGeometry.Corners(Selected).Select(ToScreen).ToArray();
            for (int i = 0; i < 4; i++)
                if (Distance(px, corners[i]) <= HandleRadius + 3)
                {
                    _resizeCorner = i;
                    Begin(Drag.Resize, px);
                    CaptureMouse();
                    return;
                }
        }

        var scene = ToScene(px);

        // topmost first, so overlapping fixtures pick the one drawn last
        var hit = Scene.Fixtures.LastOrDefault(f => LedGeometry.HitTest(f, scene));
        Select(hit);

        if (hit != null)
        {
            Begin(Drag.Move, px);
            CaptureMouse();
        }
    }

    /// <summary>
    /// Panning, on the middle button and on the right one.
    ///
    /// The right button has nothing else to do on this canvas - there is no context menu -
    /// and not every mouse has a comfortable middle click.
    /// </summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _drag = Drag.Pan;
            _dragStart = e.GetPosition(this);
            _originStart = _origin;
            CaptureMouse();
        }
        base.OnMouseDown(e);
    }

    void Begin(Drag what, Point px)
    {
        _drag = what;
        _dragStart = px;
        if (Selected != null) _before = Selected.Clone();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var px = e.GetPosition(this);

        if (_drag == Drag.Pan)
        {
            _origin = new Point(_originStart.X + (px.X - _dragStart.X), _originStart.Y + (px.Y - _dragStart.Y));
            InvalidateVisual();
            return;
        }

        if (_drag == Drag.TestPatch)
        {
            var grabbed = ToScene(_dragStart);
            var now = ToScene(px);
            TestCenter = new Point(_testStart.X + (now.X - grabbed.X), _testStart.Y + (now.Y - grabbed.Y));
            TestMoved?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (_drag == Drag.None || Selected == null) return;

        var from = ToScene(_dragStart);
        var to = ToScene(px);

        switch (_drag)
        {
            case Drag.Move:
                Selected.CenterX = _before.CenterX + (to.X - from.X);
                Selected.CenterY = _before.CenterY + (to.Y - from.Y);
                break;

            case Drag.Resize:
                Resize(to);
                break;

            case Drag.Rotate:
                double angle = Math.Atan2(to.Y - Selected.CenterY, to.X - Selected.CenterX) * 180 / Math.PI;

                // the handle sticks out of the top edge, which is 90 degrees round from zero
                angle += 90;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) angle = Math.Round(angle / 15) * 15;
                Selected.AngleDeg = angle;
                break;
        }

        FixtureChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Drags one corner while the opposite one stays put, in the fixture's own turned frame
    /// so resizing a rotated rectangle still feels straight.
    /// </summary>
    void Resize(Point sceneTo)
    {
        if (Selected == null) return;

        var local = LedGeometry.ToLocal(_before, sceneTo);

        double hw = _before.Width / 2, hh = _before.Height / 2;
        double fixedX = _resizeCorner is 0 or 3 ? hw : -hw;
        double fixedY = _resizeCorner is 0 or 1 ? hh : -hh;

        double newW = Math.Max(5, Math.Abs(local.X - fixedX));
        double newH = Math.Max(5, Math.Abs(local.Y - fixedY));

        double newLocalCx = (local.X + fixedX) / 2;
        double newLocalCy = (local.Y + fixedY) / 2;

        double rad = _before.AngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        Selected.Width = newW;
        Selected.Height = newH;
        Selected.CenterX = _before.CenterX + newLocalCx * cos - newLocalCy * sin;
        Selected.CenterY = _before.CenterY + newLocalCx * sin + newLocalCy * cos;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag != Drag.None) { _drag = Drag.None; ReleaseMouseCapture(); }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var before = ToScene(e.GetPosition(this));
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.05, 20);

        // keep the point under the cursor where it was
        var after = ToScene(e.GetPosition(this));
        _origin = new Point(_origin.X + (after.X - before.X) * _scale,
                            _origin.Y + (after.Y - before.Y) * _scale);

        InvalidateVisual();
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
