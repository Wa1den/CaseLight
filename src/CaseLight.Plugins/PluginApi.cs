using System;
using System.Collections.Generic;

namespace CaseLight.Plugins;

/// <summary>Version of this contract. A plugin built against another one is not loaded.</summary>
public static class PluginApi
{
    /// <summary>Raised on any change a plugin built earlier would break on.</summary>
    public const int Version = 1;
}

/// <summary>
/// A plugin: support for devices that the OpenRGB server does not drive.
///
/// The program finds every public non-abstract class implementing this in the DLLs of the
/// plugins folder, creates it with a parameterless constructor and calls <see cref="Start"/>.
/// Its devices then appear in the device list next to those of OpenRGB and are bound to
/// fixtures by name the same way.
/// </summary>
public interface ILightPlugin : IDisposable
{
    /// <summary><see cref="PluginApi.Version"/> the plugin was built against.</summary>
    int ApiVersion { get; }

    /// <summary>Shown in the list of plugins, e.g. "NuPhy".</summary>
    string Name { get; }

    /// <summary>One line on what the plugin drives, for the same list.</summary>
    string Description { get; }

    /// <summary>
    /// Devices present at the moment. Read from any thread; the plugin returns a snapshot,
    /// not a list it goes on changing.
    /// </summary>
    IReadOnlyList<ILightDevice> Devices { get; }

    /// <summary>
    /// Raised when a device appears or goes, from any thread. The program reads
    /// <see cref="Devices"/> again later, never inside the handler.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>
    /// Begins looking for devices. Returns at once; what takes time is done on the plugin's
    /// own thread. Exceptions are caught and the plugin is shown as failed.
    /// </summary>
    void Start();
}

/// <summary>One device of a plugin.</summary>
public interface ILightDevice
{
    /// <summary>
    /// The name fixtures are bound by. Keep it stable between runs and versions of the
    /// plugin: a changed name leaves every fixture on this device unbound.
    /// </summary>
    string Name { get; }

    /// <summary>Tells two identical devices apart, e.g. the USB port; empty if there is only ever one.</summary>
    string Location { get; }

    /// <summary>The zones, in the order their LEDs follow each other in a frame.</summary>
    IReadOnlyList<LightZone> Zones { get; }

    /// <summary>
    /// Shows a frame: three bytes R, G, B per LED of all zones in order.
    ///
    /// Must not block. It is called from the paint loop at up to the screen's frame rate,
    /// and it is expected to copy the frame and hand it over to the plugin's own thread.
    ///
    /// The frame stays on the device until the next one or <see cref="Release"/>, however
    /// long that is: a still screen produces no new frames. A device that falls back to its
    /// own effect after a timeout has to have the frame repeated by the plugin.
    /// </summary>
    void Write(ReadOnlySpan<byte> rgb);

    /// <summary>
    /// Stops driving the device and lets it go back to whatever it shows on its own. Called
    /// when the program exits and when the plugin is switched off. Stopping the painting
    /// leaves the last frame on, as it does on every other device; a later
    /// <see cref="Write"/> takes the device again.
    /// </summary>
    void Release();
}

/// <summary>A zone of a device.</summary>
/// <param name="Name">Shown in the zone list of a fixture.</param>
/// <param name="LedCount">Number of LEDs.</param>
/// <param name="Layout">
/// Where each LED physically is, one rectangle per LED in any units, e.g. millimetres or
/// key widths. Only the proportions matter. Null if the LEDs have no meaningful position.
/// </param>
public sealed record LightZone(string Name, int LedCount, IReadOnlyList<LedRect>? Layout = null);

/// <summary>The area one LED lights: for a keyboard, the key it sits under.</summary>
public readonly record struct LedRect(double X, double Y, double Width, double Height);
