using System;
using System.Text.Json.Serialization;

namespace CaseLight.Model;

/// <summary>How the LEDs of a fixture are arranged inside its rectangle.</summary>
public enum Arrangement
{
    /// <summary>A straight run with two ends - a plain strip.</summary>
    Strip,

    /// <summary>
    /// A closed contour: the run comes back to where it started, as on any fan frame.
    /// This is what makes choosing a starting LED meaningful - on a loop there is no
    /// natural first one, so which LED counts as the beginning has to be told.
    /// </summary>
    Closed,

    /// <summary>Everything at one spot - a logo, a lit badge, a DRAM module seen as a whole.</summary>
    Point
}

/// <summary>
/// Which physical LEDs a fixture drives.
///
/// Addressed by device name rather than by index on purpose: the controller list changes
/// order whenever a detector is enabled or a device disappears - disabling the Palit GPU
/// detector alone renumbered everything below it.
/// </summary>
public sealed class Binding
{
    /// <summary>Device name as OpenRGB reports it, e.g. "ASUS ROG STRIX B850-G GAMING WIFI".</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Distinguishes two identical devices; empty means "any".</summary>
    public string DeviceLocation { get; set; } = "";

    public int ZoneIndex { get; set; }

    /// <summary>First LED within the zone.</summary>
    public int FirstLed { get; set; }

    public int LedCount { get; set; }

    public Binding Clone() => (Binding)MemberwiseClone();
}

/// <summary>
/// One physical light in the case, placed on the scene as a rectangle.
///
/// The rectangle is where the thing is; <see cref="Arrangement"/> and the anchor say where
/// each individual LED sits inside it. The two are kept apart because the same 68-LED ring
/// means completely different things lying flat and standing edge-on.
/// </summary>
public sealed class Fixture
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Новая фигура";
    public Binding Binding { get; set; } = new();

    // ---- место на сцене, в миллиметрах ------------------------------------

    /// <summary>Centre of the rectangle. Rotation turns around it, so the centre is what stays put.</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }

    public double Width { get; set; } = 120;
    public double Height { get; set; } = 120;

    /// <summary>Clockwise, degrees.</summary>
    public double AngleDeg { get; set; }

    // ---- как разложены диоды ----------------------------------------------

    public Arrangement Arrangement { get; set; } = Arrangement.Strip;

    /// <summary>
    /// Which LED counts as the beginning of the run - the bottom of a closed contour, the
    /// left end of a strip. Everything else is placed relative to it.
    ///
    /// Only really matters once the contour is closed: a loop has no first LED of its own,
    /// and on a fan standing edge-on the choice decides which LED is at the bottom and
    /// therefore how the whole thing reads vertically.
    /// </summary>
    public int AnchorLed { get; set; }

    /// <summary>Reverses the direction the contour is walked in.</summary>
    public bool Reverse { get; set; }

    /// <summary>
    /// The fixture is seen from the side, so its contour collapses onto one line.
    ///
    /// This is the case for every fan in the case: they face sideways, the LEDs run around
    /// a circle, and two LEDs symmetric about the vertical axis end up at the same height.
    /// Height then follows the cosine of the angle from the anchor, not the position in
    /// the chain.
    /// </summary>
    public bool EdgeOn { get; set; }

    /// <summary>
    /// A closed contour can be a circle or a rectangular outline. A single fan's frame is
    /// round; the frame around a triple fan is a long rectangle, and its LEDs spread along
    /// the height quite differently.
    /// </summary>
    public bool RoundContour { get; set; } = true;

    /// <summary>
    /// Height over width of a rectangular contour. Only used when the contour is closed
    /// and not round.
    /// </summary>
    public double ContourAspect { get; set; } = 1.0;

    /// <summary>Purely cosmetic, so fixtures can be told apart on the canvas.</summary>
    public string Tint { get; set; } = "#4C8DFF";

    public Fixture Clone()
    {
        var copy = (Fixture)MemberwiseClone();
        copy.Binding = Binding.Clone();
        return copy;
    }

    [JsonIgnore]
    public int LedCount => Binding.LedCount;
}
