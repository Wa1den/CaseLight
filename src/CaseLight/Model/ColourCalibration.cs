using System;
using CaseLight.Core.Leds;

namespace CaseLight.Model;

/// <summary>
/// The colour balance a scene and a fixture both carry: the temperature and gains, and the
/// calibration that replaces them when switched on.
///
/// The values stay flat properties of each rather than one shared object:
/// <see cref="Scene.CopyFrom"/> copies properties by reference, and an object inside would
/// change the applied copy behind Cancel along with the live one.
/// </summary>
public interface ICalibrated
{
    int TemperatureK { get; set; }
    double GainR { get; set; }
    double GainG { get; set; }
    double GainB { get; set; }

    bool Calibration { get; set; }
    double CalWhiteR { get; set; }
    double CalWhiteG { get; set; }
    double CalWhiteB { get; set; }
    double CalHueR { get; set; }
    double CalHueG { get; set; }
    double CalHueB { get; set; }
    double CalSatR { get; set; }
    double CalSatG { get; set; }
    double CalSatB { get; set; }
}

public static class ColourCalibration
{
    /// <summary>The matrix the pipeline takes, or null while the calibration is off.</summary>
    public static ColorMatrix? Of(ICalibrated c) => c.Calibration
        ? ColorMatrix.FromPrimaries((c.CalWhiteR, c.CalWhiteG, c.CalWhiteB),
            (c.CalHueR, c.CalHueG, c.CalHueB), (c.CalSatR, c.CalSatG, c.CalSatB))
        : null;

    /// <summary>
    /// Starts the calibration white from what the temperature and gains produce now, so
    /// switching calibration on does not throw away a white already matched by eye.
    /// Normalised to the strongest channel, which the white sliders top out at. Only while
    /// the white point is still where it ships, so a matched one is never overwritten.
    /// </summary>
    public static void SeedWhite(ICalibrated c)
    {
        if (c.CalWhiteR < 1 || c.CalWhiteG < 1 || c.CalWhiteB < 1) return;

        var (tr, tg, tb) = ColorPipeline.TemperatureGains(c.TemperatureK);
        double r = tr * c.GainR, g = tg * c.GainG, b = tb * c.GainB;
        double max = Math.Max(r, Math.Max(g, b));
        if (max <= 0) return;

        c.CalWhiteR = r / max;
        c.CalWhiteG = g / max;
        c.CalWhiteB = b / max;
    }

    /// <summary>Copies the calibration of one onto the other, the balance it replaces left alone.</summary>
    public static void Copy(ICalibrated from, ICalibrated to)
    {
        to.Calibration = from.Calibration;
        to.CalWhiteR = from.CalWhiteR;
        to.CalWhiteG = from.CalWhiteG;
        to.CalWhiteB = from.CalWhiteB;
        to.CalHueR = from.CalHueR;
        to.CalHueG = from.CalHueG;
        to.CalHueB = from.CalHueB;
        to.CalSatR = from.CalSatR;
        to.CalSatG = from.CalSatG;
        to.CalSatB = from.CalSatB;
    }
}
