using System;
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
    public static Point[] Local(Fixture f)
    {
        int n = Math.Max(0, f.Binding.LedCount);
        var points = new Point[n];
        if (n == 0) return points;

        for (int i = 0; i < n; i++)
        {
            // Distance from the anchor along the run, wrapped. The anchor is LED zero of
            // the contour: the bottom of a ring, the left end of a strip.
            int step = i - f.AnchorLed;
            if (f.Reverse) step = -step;
            int k = ((step % n) + n) % n;

            points[i] = f.Arrangement switch
            {
                Arrangement.Strip => StripPoint(k, n),
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

        return points;
    }

    static Point StripPoint(int k, int n) => new((k + 0.5) / n, 0.5);

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
    public static Point[] World(Fixture f)
    {
        var local = Local(f);
        var world = new Point[local.Length];

        double rad = f.AngleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        for (int i = 0; i < local.Length; i++)
        {
            // rectangle-local, measured from the centre so rotation leaves the centre alone
            double dx = (local[i].X - 0.5) * f.Width;
            double dy = (local[i].Y - 0.5) * f.Height;

            world[i] = new Point(f.CenterX + dx * cos - dy * sin,
                                 f.CenterY + dx * sin + dy * cos);
        }

        return world;
    }

    /// <summary>
    /// How far the LEDs of a fixture reach, which is not always its rectangle.
    ///
    /// A strip runs along its middle line and has no thickness of its own; a ring seen
    /// edge-on collapses the other way, onto a vertical line. Across such a fixture there
    /// is nothing to set - what it covers there is decided by the sampling area alone.
    /// </summary>
    public static (double Width, double Height) LedSpread(Fixture f) => f.Arrangement switch
    {
        Arrangement.Point => (0, 0),
        Arrangement.Strip => (f.Width, 0),
        _ => (f.EdgeOn ? 0 : f.Width, f.Height)
    };

    /// <summary>
    /// The outline of a fixture as it is shown and grabbed: where the LEDs reach, plus the
    /// patch of screen each one averages around itself.
    ///
    /// The margin is the sampling value on every side, so a flat fixture ends up exactly as
    /// wide across as the sampling covers. That is not half of it by accident: the painting
    /// takes u ± radius around each LED, so the patch really is twice the number shown.
    /// </summary>
    public static (double Width, double Height) BoxSize(Fixture f, double reachMm)
    {
        var (w, h) = LedSpread(f);
        return (w + 2 * reachMm, h + 2 * reachMm);
    }

    /// <summary>The corners of that box on the scene, for drawing and for hit testing.</summary>
    public static Point[] BoxCorners(Fixture f, double reachMm)
    {
        var (w, h) = BoxSize(f, reachMm);
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
    public static bool HitTest(Fixture f, Point scene, double reachMm)
    {
        var p = ToLocal(f, scene);
        var (w, h) = BoxSize(f, reachMm);
        return Math.Abs(p.X) <= w / 2 && Math.Abs(p.Y) <= h / 2;
    }
}
