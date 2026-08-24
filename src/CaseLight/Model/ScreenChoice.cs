using System;
using System.Collections.Generic;
using System.Linq;
using CaseLight.Core.Capture;

namespace CaseLight.Model;

/// <summary>
/// Which screen a layout is bound to, and how to say its name.
///
/// <c>\\.\DISPLAY2</c> is not an identity. Windows hands those names out in the order it
/// finds the outputs, so moving a cable from one port of the graphics card to another
/// renumbers them - measured here, not feared: between two sessions the ultrawide and the
/// portrait screen swapped names, which would have left the capture pointed at the wrong
/// screen and the monitor rectangle sized for the wrong panel.
///
/// The model out of EDID survives that, so it is asked first and the device name is kept
/// only to tell apart two screens of the same model.
/// </summary>
public static class ScreenChoice
{
    static readonly object Lock = new();
    static List<MonitorInfo> _monitors = new();
    static long _readAt;

    /// <summary>
    /// The attached screens. Re-read a few times a second at most: this is asked from the
    /// paint thread, and enumerating monitors is a trip through the display driver.
    /// </summary>
    public static List<MonitorInfo> Monitors(bool fresh = false)
    {
        lock (Lock)
        {
            if (fresh || _monitors.Count == 0 || Environment.TickCount64 - _readAt > 3000)
            {
                _monitors = Native.EnumerateMonitors();
                _readAt = Environment.TickCount64;
            }

            return _monitors;
        }
    }

    /// <summary>The screen the settings point at, or the primary one when it is gone.</summary>
    public static MonitorInfo? Find(string deviceName, string model)
    {
        var monitors = Monitors();

        if (!string.IsNullOrWhiteSpace(model))
        {
            var sameModel = monitors.Where(m => m.Model == model).ToList();
            if (sameModel.Count == 1) return sameModel[0];

            // two of the same model: the device name is the only thing left to sort them by
            var exact = sameModel.FirstOrDefault(m => m.DeviceName == deviceName);
            if (exact != null) return exact;
        }

        return monitors.FirstOrDefault(m => m.DeviceName == deviceName)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    /// <summary>
    /// A readable name for the screen the frames belong to.
    ///
    /// The statistics used to print <c>\\.\DISPLAY2</c>, which says nothing about which
    /// screen that is - least of all after the numbering has changed underneath.
    /// </summary>
    public static string Label(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "";

        var found = Monitors().FirstOrDefault(m => m.DeviceName == deviceName)
                 ?? Monitors(fresh: true).FirstOrDefault(m => m.DeviceName == deviceName);

        return found?.DisplayName ?? deviceName;
    }
}
