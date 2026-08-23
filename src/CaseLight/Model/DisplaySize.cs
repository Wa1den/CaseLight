using System;
using System.Runtime.InteropServices;
using CaseLight.Core.Capture;

namespace CaseLight.Model;

/// <summary>
/// How large the chosen screen actually is, in millimetres.
///
/// The scene is measured in millimetres, so the monitor rectangle has to be a real size
/// rather than a guess: a fan standing beside a 34" ultrawide sees a very different part of
/// the picture than one beside a 24" 16:9, and the difference is exactly the shape of that
/// rectangle. EDID carries the physical size of the panel, so the screen can state its own
/// dimensions instead of being measured with a tape.
///
/// The registry lookup duplicates the one <c>Native.GetMonitorModel</c> already does. That
/// is deliberate: Native lives in the shared copy of the Rimlight code, which is kept
/// identical on both sides, and the monitor lighting has no use for physical size.
/// </summary>
public static class DisplaySize
{
    /// <summary>Anything smaller is a misread rather than a panel.</summary>
    const double SaneMinMm = 50;

    /// <summary>
    /// Physical size of the panel, with rotation applied.
    ///
    /// EDID describes the panel as it was built, so a screen turned on its side still
    /// reports landscape dimensions. The desktop rectangle knows the truth, and swapping
    /// the two sides to match it is the whole of the correction.
    /// </summary>
    public static bool TryMeasure(MonitorInfo monitor, out double widthMm, out double heightMm)
    {
        widthMm = heightMm = 0;
        if (!TryReadEdidSize(monitor.DeviceName, out double w, out double h)) return false;
        if (w < SaneMinMm || h < SaneMinMm) return false;

        bool screenIsPortrait = monitor.Height > monitor.Width;
        bool panelIsPortrait = h > w;

        widthMm = screenIsPortrait == panelIsPortrait ? w : h;
        heightMm = screenIsPortrait == panelIsPortrait ? h : w;
        return true;
    }

    /// <summary>
    /// The rectangle to put on the scene for this screen.
    ///
    /// Without EDID the absolute size is unknown, but the proportions are not: pixels are
    /// square on every display this program will meet, so the width is kept and the height
    /// follows the aspect ratio. Getting the shape right is what matters - it decides which
    /// part of the picture each LED looks at.
    /// </summary>
    public static (double Width, double Height) Rect(MonitorInfo monitor, double currentWidthMm)
    {
        if (TryMeasure(monitor, out double w, out double h))
        {
            ProbeLog.Log("экран", $"{monitor.DeviceName}: {monitor.Width}x{monitor.Height}, по EDID {w:F0}x{h:F0} мм");
            return (w, h);
        }

        double width = currentWidthMm >= SaneMinMm ? currentWidthMm : 600;
        double height = width * Math.Max(1, monitor.Height) / Math.Max(1, monitor.Width);

        ProbeLog.Log("экран", $"{monitor.DeviceName}: EDID без размера, высота посчитана от пропорций: {width:F0}x{height:F0} мм");
        return (width, height);
    }

    /// <summary>
    /// Reads the panel size out of the EDID blob Windows keeps for the display.
    ///
    /// The detailed timing descriptor carries millimetres and is used when it looks sane;
    /// the basic bytes at 21 and 22 hold whole centimetres and serve as the fallback. A
    /// projector reports zeroes in both, which is how "no size" arrives.
    /// </summary>
    static bool TryReadEdidSize(string deviceName, out double widthMm, out double heightMm)
    {
        widthMm = heightMm = 0;

        try
        {
            var edid = ReadEdid(deviceName);
            if (edid == null || edid.Length < 128) return false;

            // detailed timing descriptor: high nibbles of byte 68 extend bytes 66 and 67
            double dtdW = ((edid[68] & 0xF0) << 4) | edid[66];
            double dtdH = ((edid[68] & 0x0F) << 8) | edid[67];

            if (dtdW >= SaneMinMm && dtdH >= SaneMinMm)
            {
                widthMm = dtdW;
                heightMm = dtdH;
                return true;
            }

            widthMm = edid[21] * 10.0;
            heightMm = edid[22] * 10.0;
            return widthMm > 0 && heightMm > 0;
        }
        catch
        {
            // a screen that will not say how big it is is not an error worth surfacing
            return false;
        }
    }

    // Declared here rather than borrowed from Native: the same call is private over there,
    // and the shared copy is not the place to widen an API for one caller.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? device, uint num, ref DISPLAY_DEVICE info, uint flags);

    const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    static byte[]? ReadEdid(string deviceName)
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        if (!EnumDisplayDevicesW(deviceName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return null;

        // \\?\DISPLAY#SAM0E0F#5&15ec5605&0&UID4355#{guid}
        var parts = dd.DeviceID.Split('#');
        if (parts.Length < 3) return null;

        string key = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
        using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
        return reg?.GetValue("EDID") as byte[];
    }
}
