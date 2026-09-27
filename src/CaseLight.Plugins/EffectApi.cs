using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CaseLight.Plugins;

/// <summary>
/// A plugin that draws over what a device shows: an indicator, a level, a spectrum.
///
/// The program finds it the same way as <see cref="ILightPlugin"/>: a public non-abstract
/// class in the DLLs of the plugins folder, created with a parameterless constructor. It
/// gets a section of its own in the window, built from <see cref="Settings"/>, with what it
/// draws on chosen at the top: a device of a plugin or fixtures of the plan, as
/// <see cref="Target"/> says. The values are kept in the program's settings and handed over
/// through <see cref="Configure"/>.
///
/// Effects draw only while the painting runs. Pause, stop, a locked session and sleep take
/// them off along with the picture.
/// </summary>
public interface ILightEffect : IDisposable
{
    /// <summary><see cref="PluginApi.Version"/> the plugin was built against.</summary>
    int ApiVersion { get; }

    /// <summary>Name of the section and of the plugin in the list, in the interface language.</summary>
    string Name { get; }

    /// <summary>One line on what the effect shows, for the list of plugins.</summary>
    string Description { get; }

    /// <summary>Glyph of the section in Segoe Fluent Icons.</summary>
    string Icon => "\uE790";

    /// <summary>
    /// Order among effects on one device: a higher one is drawn later, on top. Indicators
    /// that take a single key go above effects that fill the keyboard.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// Device plugins the effect is meant for, by <see cref="ILightPlugin.Name"/>; one of
    /// them running is enough. Empty means any: an effect draws only on devices of plugins,
    /// so without a device plugin running it has nothing to draw on either way. The program
    /// shows a warning next to the effect while none of them runs.
    /// </summary>
    IReadOnlyList<string> Requires => [];

    /// <summary>What the effect draws on, and so what its section offers to choose.</summary>
    EffectTarget Target => EffectTarget.Device;

    /// <summary>
    /// What the section offers, top to bottom, in the interface language. Read again
    /// whenever the section is built, so labels follow a change of language.
    /// </summary>
    IReadOnlyList<EffectSetting> Settings { get; }

    /// <summary>
    /// Begins the work that takes time, on the plugin's own thread. Returns at once.
    /// Exceptions are caught and the plugin is shown as failed.
    /// </summary>
    void Start();

    /// <summary>
    /// Hands over the values of <see cref="Settings"/>: before the first
    /// <see cref="Paint"/> and after every change in the section, never at the same time
    /// as <see cref="Paint"/>.
    /// </summary>
    void Configure(EffectValues values);

    /// <summary>
    /// Draws one frame over the canvas. Called about 60 times a second for as long as the
    /// effect has a device or fixtures and the painting runs, so it must not wait for
    /// anything. An effect on fixtures is called from the paint loop, and the time it takes
    /// holds up every device.
    /// </summary>
    /// <returns>
    /// Whether anything was drawn. A device that has no picture from the screen under it
    /// is let go to its own effect when nothing on it draws.
    /// </returns>
    bool Paint(EffectCanvas canvas);
}

/// <summary>What an effect draws on.</summary>
public enum EffectTarget
{
    /// <summary>
    /// One device of a plugin, chosen by name. The canvas holds every LED of the device and
    /// its layout as the plugin reports it.
    /// </summary>
    Device,

    /// <summary>
    /// Fixtures of the plan, any number of them, on any device. The canvas holds the LEDs
    /// of the fixtures chosen, one <see cref="EffectCanvas.Parts"/> entry per fixture, with
    /// the area each LED reads the screen from as its layout: millimetres on the plan, Y
    /// growing downwards. The effect draws over the colours after brightness and the rest
    /// of the colour settings.
    /// </summary>
    Fixtures
}

/// <summary>What a setting edits and how it is shown.</summary>
public enum SettingKind
{
    /// <summary>A group heading; holds no value.</summary>
    Header,

    /// <summary>A checkbox; "1" or "0".</summary>
    Toggle,

    /// <summary>A slider from <see cref="EffectSetting.Min"/> to <see cref="EffectSetting.Max"/>.</summary>
    Slider,

    /// <summary>A list of <see cref="EffectSetting.Options"/>; the index of the one chosen.</summary>
    Choice,

    /// <summary>A colour, "#RRGGBB".</summary>
    Color,

    /// <summary>
    /// Rows of the device, from <see cref="LedGrid.Rows"/>: their numbers from zero, comma
    /// separated. Shown as a checkbox per row.
    /// </summary>
    Rows,

    /// <summary>One LED of the device, its index from zero; shown and typed from one.</summary>
    Led
}

/// <summary>One setting of an effect.</summary>
/// <param name="Key">Name the value is kept under. Keep it stable between versions.</param>
/// <param name="Kind">What it edits.</param>
/// <param name="Label">Caption in the interface language.</param>
public sealed record EffectSetting(string Key, SettingKind Kind, string Label)
{
    /// <summary>Explanation behind the question mark next to the caption.</summary>
    public string? Help { get; init; }

    /// <summary>The value until the user changes it, in the form <see cref="Kind"/> keeps.</summary>
    public string Default { get; init; } = "";

    /// <summary>Left end of a slider.</summary>
    public double Min { get; init; }

    /// <summary>Right end of a slider.</summary>
    public double Max { get; init; } = 100;

    /// <summary>Step of a slider.</summary>
    public double Step { get; init; } = 1;

    /// <summary>Unit after the value of a slider, with its leading space: " с", " %".</summary>
    public string Unit { get; init; } = "";

    /// <summary>The list of a <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>
/// The values of an effect's settings, with the defaults filled in for keys the user has
/// not touched.
/// </summary>
public sealed class EffectValues
{
    /// <summary>Key under which the program keeps the name of the device chosen.</summary>
    public const string DeviceKey = "device";

    /// <summary>Key under which the program keeps the fixtures chosen, their ids comma separated.</summary>
    public const string FixturesKey = "fixtures";

    readonly IReadOnlyDictionary<string, string> _values;
    readonly Dictionary<string, string> _defaults;

    /// <param name="values">What the user set, by key.</param>
    /// <param name="settings">The settings, for the defaults of keys not in <paramref name="values"/>.</param>
    public EffectValues(IReadOnlyDictionary<string, string> values, IEnumerable<EffectSetting> settings)
    {
        _values = values;
        _defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in settings) _defaults.TryAdd(s.Key, s.Default);
    }

    /// <summary>Name of the device the effect draws on; empty if none is chosen.</summary>
    public string Device => Raw(DeviceKey);

    /// <summary>Ids of the fixtures chosen, for an effect on <see cref="EffectTarget.Fixtures"/>.</summary>
    public string[] Fixtures => Raw(FixturesKey).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The value as it is kept; empty for a key no setting declares.</summary>
    public string Raw(string key) =>
        _values.TryGetValue(key, out var v) ? v
        : _defaults.TryGetValue(key, out var d) ? d
        : "";

    /// <summary>The value of a <see cref="SettingKind.Toggle"/>.</summary>
    public bool Bool(string key) => Raw(key) is "1" or "true" or "True";

    /// <summary>The value as a number; 0 if it is not one.</summary>
    public double Number(string key) =>
        double.TryParse(Raw(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>The value rounded to a whole number: an index of a choice or of an LED.</summary>
    public int Int(string key) => (int)Math.Round(Number(key));

    /// <summary>The value of a <see cref="SettingKind.Color"/>.</summary>
    public LightColor Color(string key) => LightColor.Parse(Raw(key));

    /// <summary>A list of numbers, as <see cref="SettingKind.Rows"/> keeps it.</summary>
    public int[] Ints(string key) => ParseInts(Raw(key));

    /// <summary>Numbers from a comma-separated list, sorted, without repeats and negatives.</summary>
    public static int[] ParseInts(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
            .Where(v => v >= 0)
            .Distinct()
            .Order()
            .ToArray();
}

/// <summary>A colour of one LED.</summary>
public readonly record struct LightColor(byte R, byte G, byte B)
{
    /// <summary>All three channels at zero.</summary>
    public static readonly LightColor Black = new(0, 0, 0);

    /// <summary>"#RRGGBB" or "RRGGBB"; black for anything else.</summary>
    public static LightColor Parse(string text)
    {
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return Black;

        return new LightColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>"#RRGGBB", the form <see cref="Parse"/> reads.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>The colour at <paramref name="t"/> of the way from this one to <paramref name="to"/>.</summary>
    public LightColor Lerp(LightColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new LightColor(
            (byte)Math.Round(R + (to.R - R) * t),
            (byte)Math.Round(G + (to.G - G) * t),
            (byte)Math.Round(B + (to.B - B) * t));
    }

    /// <summary>The colour dimmed to <paramref name="k"/> of its brightness.</summary>
    public LightColor Scale(double k) => Black.Lerp(this, k);
}

/// <summary>
/// The frame of one device that effects draw over. It starts as the picture from the screen,
/// or black where the device has none, and goes to the device once every effect on it has
/// drawn.
/// </summary>
public sealed class EffectCanvas
{
    readonly byte[] _rgb;

    /// <param name="rgb">The frame, three bytes per LED, drawn over in place.</param>
    /// <param name="layout">Where each LED is, or null.</param>
    /// <param name="rows">The rows of <paramref name="layout"/>.</param>
    /// <param name="hasPicture">Whether <paramref name="rgb"/> holds a picture from the screen.</param>
    /// <param name="seconds">Time for animation.</param>
    public EffectCanvas(byte[] rgb, IReadOnlyList<LedRect>? layout, IReadOnlyList<int[]> rows,
                        bool hasPicture, double seconds)
    {
        _rgb = rgb;
        Layout = layout;
        Rows = rows;
        HasPicture = hasPicture;
        Seconds = seconds;
    }

    /// <summary>Number of LEDs of the device.</summary>
    public int LedCount => _rgb.Length / 3;

    /// <summary>Where each LED is, as the device reports it; null if it does not.</summary>
    public IReadOnlyList<LedRect>? Layout { get; }

    /// <summary>The rows of <see cref="LedGrid.Rows"/> for this device; empty on a canvas of fixtures.</summary>
    public IReadOnlyList<int[]> Rows { get; }

    IReadOnlyList<int[]>? _parts;

    /// <summary>
    /// The LEDs grouped by where they come from: one entry per fixture on a canvas of
    /// fixtures, a single entry with every LED on a canvas of a device.
    /// </summary>
    public IReadOnlyList<int[]> Parts
    {
        get => _parts ??= [Enumerable.Range(0, LedCount).ToArray()];
        init => _parts = value;
    }

    /// <summary>Whether a picture from the screen is under the effects.</summary>
    public bool HasPicture { get; }

    /// <summary>Time in seconds on a clock that does not jump, for animation.</summary>
    public double Seconds { get; }

    /// <summary>The colour an LED has now; black outside the device.</summary>
    public LightColor Get(int led) =>
        led < 0 || led >= LedCount ? LightColor.Black : new LightColor(_rgb[led * 3], _rgb[led * 3 + 1], _rgb[led * 3 + 2]);

    /// <summary>Paints an LED over whatever it had; an index outside the device is ignored.</summary>
    public void Set(int led, LightColor c)
    {
        if (led < 0 || led >= LedCount) return;
        _rgb[led * 3] = c.R;
        _rgb[led * 3 + 1] = c.G;
        _rgb[led * 3 + 2] = c.B;
    }

    /// <summary>Lays a colour over what is there, <paramref name="alpha"/> 1 covering it entirely.</summary>
    public void Blend(int led, LightColor c, double alpha)
    {
        if (alpha <= 0) return;
        Set(led, Get(led).Lerp(c, alpha));
    }
}

/// <summary>Rows and columns of LEDs worked out from the layout a device reports.</summary>
public static class LedGrid
{
    /// <summary>
    /// The LEDs grouped into rows from the top, each row from left to right. LEDs are put
    /// in one row when the centres of their rectangles are less than half a height apart,
    /// which keeps a keyboard's rows apart and a tall Enter key in the row of its top.
    /// A device with no layout is a single row in the order of its LEDs.
    /// </summary>
    public static int[][] Rows(IReadOnlyList<LedRect>? layout, int ledCount)
    {
        if (layout == null || layout.Count != ledCount || ledCount == 0)
            return ledCount == 0 ? [] : [Enumerable.Range(0, ledCount).ToArray()];

        var order = Enumerable.Range(0, ledCount)
            .OrderBy(i => layout[i].Y + layout[i].Height / 2)
            .ToArray();

        var rows = new List<List<int>>();
        double rowCentre = double.NaN, rowHeight = 0;

        foreach (int i in order)
        {
            var r = layout[i];
            double centre = r.Y + r.Height / 2;

            if (rows.Count == 0 || Math.Abs(centre - rowCentre) >= Math.Max(rowHeight, r.Height) / 2)
            {
                rows.Add(new List<int>());
                rowCentre = centre;
                rowHeight = r.Height;
            }

            rows[^1].Add(i);
        }

        return rows.Select(row => row.OrderBy(i => layout[i].X + layout[i].Width / 2).ToArray()).ToArray();
    }

    /// <summary>
    /// Where the centre of each LED falls across the width of the device, from 0 at the
    /// left edge to 1 at the right. Without a layout the LEDs are spread evenly in order.
    /// </summary>
    public static double[] Across(IReadOnlyList<LedRect>? layout, int ledCount)
    {
        var x = new double[ledCount];
        if (layout == null || layout.Count != ledCount || ledCount == 0)
        {
            for (int i = 0; i < ledCount; i++) x[i] = ledCount == 1 ? 0.5 : (i + 0.5) / ledCount;
            return x;
        }

        double left = layout.Min(r => r.X), right = layout.Max(r => r.X + r.Width);
        double width = Math.Max(1e-9, right - left);

        for (int i = 0; i < ledCount; i++) x[i] = (layout[i].X + layout[i].Width / 2 - left) / width;
        return x;
    }
}
