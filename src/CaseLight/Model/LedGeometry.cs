using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CaseLight.Model;

/// <summary>
/// Works out where each LED of a fixture physically sits.
///
/// Two steps, deliberately separate. First the LED is placed inside the fixture's own
/// rectangle in 0..1 coordinates, which is where the arrangement matters. Then that is
/// scaled, rotated and moved onto the scene, which is the same arithmetic for everything.
/// </summary>
public static class LedGeometry
{
    /// <summary>
    /// Positions inside the fixture's rectangle, in 0..1 with y growing downwards - screen
    /// convention, so the drawing code needs no flipping.
    /// </summary>
    /// <param name="bySize">
    /// The rectangle is the sampling area (<see cref="Scene.SampleBySize"/>). The LEDs are
    /// then set in from its edges by the same distance as lies between them.
    /// </param>
    public static Point[] Local(Fixture f, bool bySize = false)
    {
        int n = Math.Max(0, f.Binding.LedCount);
        var points = new Point[n];
        if (n == 0) return points;

        // У матрицы порядок обхода задан самим устройством: начало и направление к ней не
        // применяются, как и вид с торца.
        if (f.Arrangement == Arrangement.Matrix) return MatrixPoints(f, n);

        for (int i = 0; i < n; i++)
        {
            // Distance from the anchor along the run, wrapped. The anchor is LED zero of
            // the contour: the bottom of a ring, the left end of a strip.
            int step = i - f.AnchorLed;
            if (f.Reverse) step = -step;
            int k = ((step % n) + n) % n;

            points[i] = f.Arrangement switch
            {
                Arrangement.Strip => bySize ? new Point((k + 1.0) / (n + 1), 0.5) : StripPoint(k, n),
                Arrangement.Closed => f.RoundContour ? RingPoint(k, n) : PerimeterPoint(k, n, f.ContourAspect),
                _ => new Point(0.5, 0.5)
            };
        }

        if (f.EdgeOn)
        {
            // Seen from the side the contour has no width left: every LED collapses onto
            // the centre line and only its height still carries information.
            for (int i = 0; i < n; i++)
                points[i] = new Point(0.5, points[i].Y);
        }

        if (bySize && f.Arrangement == Arrangement.Closed)
        {
            // Отступ от края равен среднему шагу между рядами диодов, как у полосы. Без него
            // крайние диоды кольца стоят на самом краю рамки, где ряды теснее всего, и читают
            // полосу в миллиметр-два высотой.
            double sx = f.EdgeOn ? 0 : Step(points.Select(p => p.X));
            double sy = Step(points.Select(p => p.Y));

            for (int i = 0; i < n; i++)
                points[i] = new Point(sx + points[i].X * (1 - 2 * sx),
                                      sy + points[i].Y * (1 - 2 * sy));
        }

        return points;
    }

    const double Same = 1e-9;

    /// <summary>
    /// The average distance between neighbouring rows of LEDs along one axis, in 0..1, once
    /// the gaps at both edges are made the same size as it.
    /// </summary>
    static double Step(IEnumerable<double> values) => 1.0 / (Rows(values) + 1);

    /// <summary>How many different positions the LEDs take along one axis.</summary>
    static int Rows(IEnumerable<double> values)
    {
        int rows = 0;
        double last = double.NaN;

        foreach (double v in values.OrderBy(v => v))
        {
            if (rows > 0 && v - last < Same) continue;
            rows++;
            last = v;
        }

        return Math.Max(1, rows);
    }

    static Point StripPoint(int k, int n) => new((k + 0.5) / n, 0.5);

    /// <summary>
    /// The LEDs of a matrix: from the stored layout while it matches the LED count, otherwise
    /// a grid filled row by row from the top left, with columns chosen so the cells come out
    /// close to square in the fixture's rectangle.
    /// </summary>
    static Point[] MatrixPoints(Fixture f, int n)
    {
        var points = new Point[n];

        if (f.Layout is { } layout && layout.Length == n && layout.All(p => p.Length == 2))
        {
            for (int i = 0; i < n; i++) points[i] = new Point(layout[i][0], layout[i][1]);
            return points;
        }

        double aspect = Math.Max(f.Width, 1e-6) / Math.Max(f.Height, 1e-6);
        int cols = Math.Clamp((int)Math.Round(Math.Sqrt(n * aspect)), 1, n);
        int rows = (n + cols - 1) / cols;

        for (int i = 0; i < n; i++)
            points[i] = new Point((i % cols + 0.5) / cols, (i / cols + 0.5) / rows);

        return points;
    }

    /// <summary>
    /// Turns the rectangles a device gives for its LEDs (<c>ZoneLayout</c>) into
    /// <see cref="Fixture.Layout"/>: the centre of each, with the box around all of them
    /// stretched over 0..1.
    ///
    /// The box is taken to the outer edges of the rectangles, not to the centres, so a row of
    /// keys keeps half a key on either side, as LEDs of a strip keep half a step.
    /// </summary>
    public static double[][]? FitLayout(IReadOnlyList<Rect> cells)
    {
        if (cells.Count == 0) return null;

        double left = cells.Min(c => c.Left), right = cells.Max(c => c.Right);
        double top = cells.Min(c => c.Top), bottom = cells.Max(c => c.Bottom);
        double w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return null;

        return cells.Select(c => new[]
        {
            (c.Left + c.Width / 2 - left) / w,
            (c.Top + c.Height / 2 - top) / h
        }).ToArray();
    }

    /// <summary>
    /// A circle walked from the bottom. At k = 0 the angle is zero and the point is at the
    /// bottom; half way round it reaches the top. Height therefore follows the cosine, so
    /// LEDs crowd together near the bottom and top and spread out at the sides - which is
    /// exactly what a fan ring looks like edge-on.
    /// </summary>
    static Point RingPoint(int k, int n)
    {
        double theta = 2 * Math.PI * k / n;
        return new Point(0.5 + 0.5 * Math.Sin(theta),
                         0.5 + 0.5 * Math.Cos(theta));
    }

    /// <summary>
    /// A rectangular outline walked from the middle of the bottom edge, going right first.
    ///
    /// <paramref name="aspect"/> is the contour's height over its width. A triple fan's
    /// frame is tall and narrow, so most of its LEDs sit on the two long sides and its
    /// height reads almost linearly - unlike a circle, where the ends bunch up.
    /// </summary>
    static Point PerimeterPoint(int k, int n, double aspect)
    {
        aspect = Math.Clamp(aspect, 0.01, 100.0);

        // walk in a box 1 wide and `aspect` tall, y upwards from the bottom
        double perimeter = 2 + 2 * aspect;
        double s = perimeter * k / n;

        double x, y;

        if (s < 0.5) { x = 0.5 + s; y = 0; }                                  // низ, вправо
        else if (s < 0.5 + aspect) { x = 1; y = s - 0.5; }                    // правая вверх
        else if (s < 1.5 + aspect) { x = 1 - (s - 0.5 - aspect); y = aspect; } // верх, влево
        else if (s < 1.5 + 2 * aspect) { x = 0; y = aspect - (s - 1.5 - aspect); } // левая вниз
        else { x = s - 1.5 - 2 * aspect; y = 0; }                             // низ, до старта

        // normalise the height back into 0..1 and flip to screen coordinates
        return new Point(x, 1 - y / aspect);
    }

    /// <summary>Positions on the scene, in millimetres.</summary>
    public static Point[] World(Fixture f, bool bySize = false)
    {
        var local = Local(f, bySize);
        var world = new Point[local.Length];

        for (int i = 0; i < local.Length; i++)
            world[i] = ToScene(f, local[i]);

        return world;
    }

    /// <summary>A point of the fixture's rectangle, given in 0..1, placed on the scene in millimetres.</summary>
    public static Point ToScene(Fixture f, Point unit)
    {
        double rad = f.AngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        // rectangle-local, measured from the centre so rotation leaves the centre alone
        double dx = (unit.X - 0.5) * f.Width;
        double dy = (unit.Y - 0.5) * f.Height;

        return new Point(f.CenterX + dx * cos - dy * sin,
                         f.CenterY + dx * sin + dy * cos);
    }

    // ---- выборка по размеру фигуры ----------------------------------------

    /// <summary>
    /// The part of the fixture's rectangle each LED reads, in 0..1, when the sampling area
    /// is taken from the size of the fixture (<see cref="Scene.SampleBySize"/>).
    ///
    /// The cells cover the rectangle between them: an LED gets the part that is nearer to
    /// it than to any other. Along a strip that is a slice of the length with the full
    /// width across, the two at the ends reaching to the edges; on a ring seen edge-on it
    /// is a band of the full width, no thinner than the average step between the LEDs. A
    /// flat contour has LEDs all round, and there each one reads towards the middle.
    /// </summary>
    public static Rect[] Cells(Fixture f)
    {
        var points = Local(f, bySize: true);
        var cells = new Rect[points.Length];
        if (points.Length == 0) return cells;

        switch (f.Arrangement)
        {
            case Arrangement.Point:
                Array.Fill(cells, new Rect(0, 0, 1, 1));
                break;

            case Arrangement.Strip:
            {
                var spans = Split(points.Select(p => p.X).ToArray());
                for (int i = 0; i < cells.Length; i++)
                    cells[i] = new Rect(spans[i].Lo, 0, spans[i].Hi - spans[i].Lo, 1);
                break;
            }

            case Arrangement.Closed when f.EdgeOn:
            {
                var spans = Split(points.Select(p => p.Y).ToArray());
                for (int i = 0; i < cells.Length; i++)
                    cells[i] = new Rect(0, spans[i].Lo, 1, spans[i].Hi - spans[i].Lo);
                break;
            }

            default:
                cells = Nearest(points, f.Width, f.Height);
                break;
        }

        return cells;
    }

    /// <summary>
    /// Splits 0..1 between the values along one axis, halfway between neighbours. Values
    /// that coincide share a span: the two sides of a ring seen edge-on sit at one height.
    ///
    /// No span is narrower than the average step between the values. Along a strip every
    /// span in the middle is exactly that, and the two at the ends reach the edges; on a
    /// ring seen edge-on the LEDs near the top and bottom are closer together, and their
    /// spans overlap instead of shrinking to a band too thin to average anything.
    /// </summary>
    static (double Lo, double Hi)[] Split(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();

        // группы совпадающих значений, по возрастанию
        var groups = new List<List<int>>();
        foreach (int i in order)
        {
            if (groups.Count > 0 && values[i] - values[groups[^1][0]] < Same) groups[^1].Add(i);
            else groups.Add(new List<int> { i });
        }

        var spans = new (double Lo, double Hi)[values.Length];
        for (int g = 0; g < groups.Count; g++)
        {
            double v = values[groups[g][0]];
            double lo = g == 0 ? 0 : (values[groups[g - 1][0]] + v) / 2;
            double hi = g == groups.Count - 1 ? 1 : (v + values[groups[g + 1][0]]) / 2;

            double even = 1.0 / (groups.Count + 1);
            if (hi - lo < even)
            {
                // вокруг самого диода, не выходя за рамку
                lo = Math.Clamp(v - even / 2, 0, 1 - even);
                hi = lo + even;
            }

            foreach (int i in groups[g]) spans[i] = (lo, hi);
        }

        return spans;
    }

    /// <summary>
    /// The part of the rectangle nearest to each LED, reduced to its bounds, because a zone
    /// of the sampler is a rectangle. Found on a grid, with distance measured in
    /// millimetres, so a long frame is not split as if it were square.
    ///
    /// An LED too close to its neighbours to be the nearest to any grid node still reads the
    /// spot it sits on.
    /// </summary>
    static Rect[] Nearest(Point[] points, double width, double height)
    {
        const int Grid = 64;
        const double Half = 0.5 / Grid;

        int n = points.Length;
        width = Math.Max(width, 1e-6);
        height = Math.Max(height, 1e-6);

        var x0 = new double[n]; var x1 = new double[n];
        var y0 = new double[n]; var y1 = new double[n];
        for (int i = 0; i < n; i++)
        {
            x0[i] = x1[i] = points[i].X;
            y0[i] = y1[i] = points[i].Y;
        }

        for (int gy = 0; gy < Grid; gy++)
        for (int gx = 0; gx < Grid; gx++)
        {
            double px = (gx + 0.5) / Grid, py = (gy + 0.5) / Grid;

            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double dx = (px - points[i].X) * width;
                double dy = (py - points[i].Y) * height;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }

            x0[best] = Math.Min(x0[best], px - Half); x1[best] = Math.Max(x1[best], px + Half);
            y0[best] = Math.Min(y0[best], py - Half); y1[best] = Math.Max(y1[best], py + Half);
        }

        var cells = new Rect[n];
        for (int i = 0; i < n; i++)
        {
            double l = Math.Clamp(x0[i], 0, 1), r = Math.Clamp(x1[i], 0, 1);
            double t = Math.Clamp(y0[i], 0, 1), b = Math.Clamp(y1[i], 0, 1);
            cells[i] = new Rect(l, t, r - l, b - t);
        }

        return cells;
    }

    /// <summary>
    /// A cell placed on the scene, as the upright rectangle around it: that is the shape a
    /// sampling zone has. On a turned fixture the zone is therefore larger than the cell.
    /// </summary>
    public static Rect CellOnScene(Fixture f, Rect cell)
    {
        var a = ToScene(f, cell.TopLeft);
        var b = ToScene(f, cell.TopRight);
        var c = ToScene(f, cell.BottomRight);
        var d = ToScene(f, cell.BottomLeft);

        double left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        double right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        double top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        double bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Whether the LEDs spread along the fixture's width, in the usual mode.</summary>
    public static bool HasWidth(Fixture f) => f.Arrangement switch
    {
        Arrangement.Point => false,
        Arrangement.Strip or Arrangement.Matrix => true,
        _ => !f.EdgeOn
    };

    /// <summary>Whether the LEDs spread along the fixture's height, in the usual mode.</summary>
    public static bool HasHeight(Fixture f) => f.Arrangement is Arrangement.Closed or Arrangement.Matrix;

    /// <summary>
    /// How far the LEDs of a fixture reach, which is not always its rectangle.
    ///
    /// A strip runs along its middle line and has no thickness of its own; a ring seen
    /// edge-on collapses the other way, onto a vertical line. Across such a fixture there
    /// is nothing to set - what it covers there is decided by the sampling area alone.
    /// </summary>
    public static (double Width, double Height) LedSpread(Fixture f) =>
        (HasWidth(f) ? f.Width : 0, HasHeight(f) ? f.Height : 0);

    /// <summary>
    /// The outline of a fixture as it is shown and grabbed: where the LEDs reach, plus the
    /// patch of screen each one averages around itself.
    ///
    /// The margin is the sampling value on every side, so a flat fixture ends up exactly as
    /// wide across as the sampling covers. That is not half of it by accident: the painting
    /// takes u ± radius around each LED, so the patch really is twice the number shown.
    ///
    /// With the sampling taken from the size of the fixture the rectangle is the outline
    /// itself, and every fixture has both sides to set.
    /// </summary>
    public static (double Width, double Height) BoxSize(Fixture f, Scene scene) =>
        scene.SampleBySize ? (f.Width, f.Height) : MarginBox(f, scene.SampleRadiusMm);

    /// <summary>The outline in the usual mode: where the LEDs reach, plus the margin on every side.</summary>
    public static (double Width, double Height) MarginBox(Fixture f, double reachMm)
    {
        var (w, h) = LedSpread(f);
        return (w + 2 * reachMm, h + 2 * reachMm);
    }

    /// <summary>The corners of that box on the scene, for drawing and for hit testing.</summary>
    public static Point[] BoxCorners(Fixture f, Scene scene)
    {
        var (w, h) = BoxSize(f, scene);
        return Rect(f, w / 2, h / 2);
    }

    /// <summary>The fixture's four corners on the scene, without the sampling margin.</summary>
    public static Point[] Corners(Fixture f) => Rect(f, f.Width / 2, f.Height / 2);

    static Point[] Rect(Fixture f, double hw, double hh)
    {
        double rad = f.AngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        var local = new[]
        {
            new Point(-hw, -hh), new Point(hw, -hh),
            new Point(hw, hh), new Point(-hw, hh)
        };

        var corners = new Point[4];
        for (int i = 0; i < 4; i++)
            corners[i] = new Point(f.CenterX + local[i].X * cos - local[i].Y * sin,
                                   f.CenterY + local[i].X * sin + local[i].Y * cos);

        return corners;
    }

    /// <summary>Turns a scene point into the fixture's own unrotated frame, for hit testing.</summary>
    public static Point ToLocal(Fixture f, Point scene)
    {
        double rad = -f.AngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        double dx = scene.X - f.CenterX;
        double dy = scene.Y - f.CenterY;

        return new Point(dx * cos - dy * sin, dx * sin + dy * cos);
    }

    /// <summary>Whether the point lands on the fixture as it is drawn, margin included.</summary>
    public static bool HitTest(Fixture f, Point at, Scene scene)
    {
        var p = ToLocal(f, at);
        var (w, h) = BoxSize(f, scene);
        return Math.Abs(p.X) <= w / 2 && Math.Abs(p.Y) <= h / 2;
    }
}
