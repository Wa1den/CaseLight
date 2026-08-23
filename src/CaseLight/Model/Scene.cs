using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaseLight.Model;

/// <summary>Where the frames come from.</summary>
public enum CaptureSource
{
    /// <summary>Rimlight publishes them on the shared bus; nothing is captured here.</summary>
    FromRimlight,

    /// <summary>Our own capture: DDA and WGC together, GDI covering the gaps.</summary>
    Auto,
    DdaOnly,
    WgcOnly,
    GdiOnly
}

/// <summary>
/// Reads the source by name, answering to the name it used to have.
///
/// The shared project was renamed from Ambilight to Rimlight, and settings written before
/// that say "FromAmbilight". A name the enum does not know makes deserialisation throw, and
/// <see cref="Scene.Load"/> answers a broken file with an empty scene - so without this the
/// rename alone would quietly cost the user their whole layout on the next launch.
///
/// An unknown name falls back to the default rather than throwing, for the same reason.
/// </summary>
sealed class CaptureSourceConverter : JsonConverter<CaptureSource>
{
    public override CaptureSource Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return (CaptureSource)reader.GetInt32();

        string name = reader.GetString() ?? "";

        if (name.Equals("FromAmbilight", StringComparison.OrdinalIgnoreCase))
            return CaptureSource.FromRimlight;

        return Enum.TryParse(name, ignoreCase: true, out CaptureSource value)
            ? value
            : CaptureSource.FromRimlight;
    }

    public override void Write(Utf8JsonWriter writer, CaptureSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// What to do with the server after the machine wakes up.
///
/// Sleep re-enumerates the USB controllers, and a server that stayed running keeps writing
/// into handles that lead nowhere: it reports success while the case sits in the pattern it
/// shows during boot. Something has to shake it.
/// </summary>
public enum WakeRecovery
{
    /// <summary>Just resume - fine if the lighting survives sleep on this machine.</summary>
    Nothing,

    /// <summary>Ask the server to look for hardware again. Gentler, but it can crash it.</summary>
    Rescan,

    /// <summary>Close the server and start it fresh. Blunt and reliable.</summary>
    RestartServer
}

/// <summary>Shape of the movable test patch used to check placement.</summary>
public enum TestShape { Circle, Square }

/// <summary>
/// The monitor, placed on the same plane as the case.
///
/// Everything is positioned relative to it, because the whole point is for the case to
/// read as a continuation of the screen: an LED to the right of the monitor's right edge
/// should show what is happening at that edge, and it can only know that if it knows where
/// the monitor is.
/// </summary>
public sealed class MonitorPlacement
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }

    /// <summary>Visible picture, in millimetres. A 27" 16:9 panel is about 597 x 336.</summary>
    public double Width { get; set; } = 597;
    public double Height { get; set; } = 336;

    public MonitorPlacement Clone() => (MonitorPlacement)MemberwiseClone();
}

/// <summary>
/// Everything the application knows: the layout in millimetres and every setting.
///
/// One object, so "apply" and "cancel" have something whole to snapshot, and so export is
/// a single file that restores the program exactly.
/// </summary>
public sealed class Scene
{
    // ---- раскладка --------------------------------------------------------

    public MonitorPlacement Monitor { get; set; } = new();
    public List<Fixture> Fixtures { get; set; } = new();

    // ---- захват -----------------------------------------------------------

    public CaptureSource CaptureSource { get; set; } = CaptureSource.FromRimlight;
    public int MaxFps { get; set; } = 30;

    /// <summary>Which screen to capture ourselves. Empty means the primary one.</summary>
    public string MonitorDeviceName { get; set; } = "";

    // ---- раскраска --------------------------------------------------------

    /// <summary>
    /// How much of the screen one LED averages over, in millimetres of scene.
    ///
    /// A single pixel would make the case flicker on every small movement; too wide and
    /// everything turns into the same brown average. Roughly the size of the lit object
    /// itself works well.
    /// </summary>
    public double SampleRadiusMm { get; set; } = 60;

    public double Brightness { get; set; } = 1.0;
    public double Gamma { get; set; } = 2.2;
    public double Saturation { get; set; } = 1.15;
    public double MinLuma { get; set; }
    public int TemperatureK { get; set; } = 6500;
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;

    /// <summary>Light rises quickly and falls gently, as in Rimlight.</summary>
    public double SmoothingRise { get; set; } = 0.55;
    public double SmoothingFall { get; set; } = 0.18;

    /// <summary>
    /// Whether fixtures switched out of the painting are still drawn on the canvas.
    ///
    /// A layout is easier to read without them, but they must not disappear altogether:
    /// the selected one is always drawn, otherwise picking it from the list would point at
    /// an empty patch of canvas.
    /// </summary>
    public bool ShowDisabled { get; set; } = true;

    // ---- тест размещения --------------------------------------------------

    public TestShape TestShape { get; set; } = TestShape.Circle;

    /// <summary>Diameter or side of the test patch, in millimetres of scene.</summary>
    public double TestSizeMm { get; set; } = 150;

    public string TestColor { get; set; } = "#FF4020";

    // ---- основное ---------------------------------------------------------

    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool WriteLog { get; set; } = true;

    /// <summary>
    /// Begin painting as soon as the window opens.
    ///
    /// Pointless on its own, but with autostart it is the difference between the case
    /// lighting up by itself and having to press a button after every reboot.
    /// </summary>
    public bool StartPaintingOnLaunch { get; set; }

    // ---- сервер OpenRGB ---------------------------------------------------

    /// <summary>Start the server ourselves when it is not up.</summary>
    public bool AutoStartOpenRgb { get; set; } = true;

    /// <summary>Empty means "find it yourself".</summary>
    public string OpenRgbPath { get; set; } = "";

    /// <summary>
    /// Only worth it for the SMBus, which is to say for DRAM. Everything else - the
    /// motherboard controller, the graphics card - is reachable without elevation, and
    /// staying unelevated means no UAC prompt at every login.
    /// </summary>
    public bool OpenRgbAsAdmin { get; set; }

    // ---- питание ----------------------------------------------------------

    public bool OffOnExit { get; set; } = true;
    public bool OffOnDisplayOff { get; set; } = true;
    public bool OffOnLock { get; set; } = true;
    public bool OffOnSuspend { get; set; } = true;

    /// <summary>
    /// How long to wait after a wake before writing to the hardware again.
    ///
    /// Not politeness: OpenRGB was seen dying 41 seconds after a resume with an access
    /// violation, because its USB devices are re-enumerated while it still holds the old
    /// handles. Letting the bus settle first is the cheapest way not to be the one poking
    /// it at that moment.
    /// </summary>
    public int ResumeDelayMs { get; set; } = 8000;

    /// <summary>Default is the blunt one, because it is the one that actually works.</summary>
    public WakeRecovery WakeRecovery { get; set; } = WakeRecovery.RestartServer;

    // ---- геометрия окна ---------------------------------------------------

    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;

    // nullable rather than NaN: System.Text.Json refuses to write NaN
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    // ---- хранение ---------------------------------------------------------

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // Order matters: the list is walked front to back and the first converter that
        // accepts the type wins. JsonStringEnumConverter claims every enum, so the one
        // that knows the old name has to stand ahead of it.
        Converters = { new CaptureSourceConverter(), new JsonStringEnumConverter() }
    };

    [JsonIgnore]
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaseLight");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(Folder, "scene.json");

    [JsonIgnore]
    public static string LogPath => Path.Combine(Folder, "caselight.log");

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public static Scene Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Options) ?? new Scene();
        }
        catch
        {
            // A broken file must not cost the user their session; they can always re-save
            // over it, and losing the layout silently is worse than starting empty.
        }
        return new Scene();
    }

    /// <summary>Throws on a bad file, so import can tell the user what went wrong.</summary>
    public static Scene Import(string path) =>
        JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException("файл пуст или не содержит настроек");

    // ---- применить и отменить ---------------------------------------------

    /// <summary>Independent copy, used as the "last applied" snapshot behind Cancel.</summary>
    public Scene Clone()
    {
        var copy = new Scene();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>
    /// Copies every value onto this instance, keeping the object identity the running
    /// painter already holds.
    ///
    /// The fixture list is rebuilt from copies rather than shared: cancelling has to undo
    /// dragging on the canvas too, and that only works if the snapshot owns its own
    /// fixtures.
    /// </summary>
    public void CopyFrom(Scene other)
    {
        foreach (var prop in typeof(Scene).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name is nameof(Fixtures) or nameof(Monitor)) continue;

            prop.SetValue(this, prop.GetValue(other));
        }

        Monitor = other.Monitor.Clone();

        var rebuilt = other.Fixtures.Select(f => f.Clone()).ToList();
        lock (Fixtures)
        {
            Fixtures.Clear();
            Fixtures.AddRange(rebuilt);
        }
    }

    /// <summary>True when anything at all differs - what turns the apply bar on.</summary>
    public bool DiffersFrom(Scene other) =>
        JsonSerializer.Serialize(this, Options) != JsonSerializer.Serialize(other, Options);
}
