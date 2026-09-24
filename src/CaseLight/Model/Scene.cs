using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using CaseLight.Core.Text;
using CaseLight.Render;

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

    /// <summary>
    /// Ask the server to look for hardware again, without restarting it.
    ///
    /// OpenRGB 1.0 deletes every controller on this request and detects them afresh, and it
    /// survived that with frames still being written. 0.9 died on the same request three
    /// times out of three, so the rescan is only sent to a server on protocol 6, the version
    /// that also announces the end of detection; an older one is left as it is.
    /// </summary>
    Rescan,

    /// <summary>Close the server and start it fresh. Blunt and reliable.</summary>
    RestartServer,

    /// <summary>
    /// Rescan, and restart the server if that did not bring the devices back: no protocol 6,
    /// no end of detection in time, or fewer devices than before sleep.
    /// </summary>
    RescanThenRestart
}

/// <summary>Shape of the movable test patch used to check placement.</summary>
public enum TestShape { Circle, Square }

/// <summary>
/// The material Windows fills the window background with.
///
/// There is no plain colour among them. The system caption buttons are drawn under the
/// client area, so an opaque window background covers them, and with the background left
/// transparent and no material the frame shows through black in either theme.
/// </summary>
public enum WindowBackdrop
{
    Mica,

    /// <summary>Mica tinted more strongly, the one Windows gives windows with tabs in the title bar.</summary>
    MicaAlt,
    Acrylic
}

/// <summary>
/// Reads the backdrop by name, falling back to the default on a name it does not know.
///
/// The stock enum converter throws on an unknown name, and <see cref="Scene.Load"/> answers
/// any exception with an empty scene - a settings file from a later version with one more
/// material in the list would cost the whole layout.
/// </summary>
sealed class WindowBackdropConverter : JsonConverter<WindowBackdrop>
{
    public override WindowBackdrop Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int n = reader.GetInt32();
            return Enum.IsDefined(typeof(WindowBackdrop), n) ? (WindowBackdrop)n : WindowBackdrop.MicaAlt;
        }

        return Enum.TryParse(reader.GetString(), ignoreCase: true, out WindowBackdrop value)
            ? value
            : WindowBackdrop.MicaAlt;
    }

    public override void Write(Utf8JsonWriter writer, WindowBackdrop value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

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

    /// <summary>
    /// Ceiling on how often the case is repainted, in frames per second. Zero removes the
    /// ceiling and leaves only the floor of the paint loop itself.
    ///
    /// The ceiling is what saves graphics card work on own capture, and it is paid for in
    /// latency: a frame that arrives inside the throttle window is dropped rather than
    /// held, so the picture waits for the next one. Slow buses are not what this is for -
    /// memory on the SMBus gets a divider of its own in <see cref="Fixture.UpdateEvery"/>.
    /// </summary>
    public int MaxFps { get; set; }

    /// <summary>Which screen to capture ourselves. Empty means the primary one.</summary>
    public string MonitorDeviceName { get; set; } = "";

    /// <summary>
    /// Model of that screen out of EDID, which is what actually identifies it.
    ///
    /// The device name beside it is not an identity: Windows renumbers the outputs when
    /// cables move between ports. See <see cref="ScreenChoice"/>.
    /// </summary>
    public string MonitorModel { get; set; } = "";

    // ---- раскраска --------------------------------------------------------

    /// <summary>
    /// How much of the screen one LED averages over, in millimetres of scene.
    ///
    /// A single pixel would make the case flicker on every small movement; too wide and
    /// everything turns into the same brown average. Roughly the size of the lit object
    /// itself works well.
    /// </summary>
    public double SampleRadiusMm { get; set; } = 20;

    /// <summary>
    /// Take the sampling area from the size of each fixture instead of <see cref="SampleRadiusMm"/>.
    ///
    /// The rectangle of a fixture is then the whole area it reads, and it is divided
    /// between its LEDs (<see cref="LedGeometry.Cells"/>). A strip made wider reads a wider band
    /// of the screen, which the radius could only give to every fixture at once. The meaning
    /// of the fixture sizes changes with it, so it is switched by
    /// <see cref="SetSampleBySize"/>, never set directly.
    /// </summary>
    public bool SampleBySize { get; set; }

    /// <summary>
    /// Switches <see cref="SampleBySize"/> and converts every fixture, so the outlines on the
    /// canvas stay where they were.
    ///
    /// On the way in the outline, LEDs plus the sampling margin, becomes the rectangle. On
    /// the way out the margin is taken back off the sides the LEDs spread along; across a
    /// strip or a ring seen edge-on the size is the sampling area's again.
    /// </summary>
    public void SetSampleBySize(bool on)
    {
        if (on == SampleBySize) return;

        double margin = 2 * SampleRadiusMm;

        lock (Fixtures)
            foreach (var f in Fixtures)
            {
                if (on)
                {
                    (f.Width, f.Height) = LedGeometry.MarginBox(f, SampleRadiusMm);
                    continue;
                }

                if (LedGeometry.HasWidth(f)) f.Width = Math.Max(1, f.Width - margin);
                if (LedGeometry.HasHeight(f)) f.Height = Math.Max(1, f.Height - margin);
            }

        SampleBySize = on;
    }

    /// <summary>
    /// How the frame is worked over before the zones are read, in percent from -50 to 50.
    /// Zero takes the picture as captured.
    ///
    /// Below zero it is defocused: an LED then draws its colour from the neighbourhood
    /// around its sampling area without that area growing, and neighbouring lights run into
    /// each other. Above zero it is sharpened: neighbouring zones are pushed apart, so a
    /// light patch beside a dark one reads brighter. See <see cref="FrameFilter"/>.
    /// </summary>
    public int Sharpness { get; set; }

    public double Brightness { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double MinLuma { get; set; }

    /// <summary>
    /// Level no channel of an LED drops below, 0..1 of the output scale. Zero switches it
    /// off.
    ///
    /// Works with <see cref="MinLuma"/> rather than against it: the threshold decides what
    /// counts as black, and this decides what black looks like. The lift is one amount
    /// added to all three channels, so a dark scene that still carries a colour keeps it.
    /// </summary>
    public double MinBacklight { get; set; }

    /// <summary>
    /// How far up the scale the colour is faded out of the shadows.
    ///
    /// White balance is a proportion, so it tints every level alike - and where the picture
    /// is almost black, the tint is the only thing left to see. A player bar, black with
    /// white digits, averages to a dark grey, and a warm balance turns that grey into a
    /// dark red on the case. Fading the colour back towards grey as the level falls settles
    /// the shadows and leaves real content alone, since there the level is high.
    ///
    /// Zero switches it off, which is how it behaved before.
    /// </summary>
    public double ShadowNeutral { get; set; }
    public int TemperatureK { get; set; } = 5600;
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;

    public double SmoothingRise { get; set; } = 0.9;
    public double SmoothingFall { get; set; } = 0.9;

    // ---- кадрирование -----------------------------------------------------

    /// <summary>
    /// Follows the black bars of letterboxed material and samples the picture inside them
    /// instead of the whole screen. Off by default: it changes where every LED reads from,
    /// which is not something to switch on behind the back of a layout already tuned by
    /// hand.
    /// </summary>
    public bool AdaptiveCrop { get; set; }

    /// <summary>Letterboxing - bars above and below. The common case, so on by default.</summary>
    public bool CropVertical { get; set; } = true;

    /// <summary>Pillarboxing - bars left and right. 4:3 material on a wide screen.</summary>
    public bool CropHorizontal { get; set; } = true;

    /// <summary>Below this a bar is taken for a dark edge and ignored. Percent of the side.</summary>
    public double CropMinPercent { get; set; } = 2.0;

    /// <summary>Ceiling on the crop, as a percent of the side.</summary>
    public double CropMaxPercent { get; set; } = 14.0;

    /// <summary>Per-channel value below which a pixel counts as black.</summary>
    public int CropBlackLevel { get; set; } = 16;

    /// <summary>
    /// How much of a lit run inside the bar is stepped over rather than taken for the edge
    /// of the picture - subtitles, the progress bar, the buttons of a player. Percent of
    /// the side.
    /// </summary>
    public double CropOverlookPercent { get; set; } = 8.0;

    /// <summary>How long a new reading has to hold before the sampling moves.</summary>
    public double CropHoldMs { get; set; } = 700;

    /// <summary>Extra margin taken inside the picture once a bar is found, percent of the side.</summary>
    public double CropInsetPercent { get; set; } = 0.5;

    /// <summary>
    /// Spreads the picture across the whole layout, so an LED that reads from behind a bar
    /// takes the nearest part of the picture instead of sitting dark. With this off the
    /// sampling only slides clear of the bars and keeps its places otherwise.
    /// </summary>
    public bool CropStretch { get; set; } = true;

    /// <summary>The subset the detector reads, on the same footing as the colour settings.</summary>
    public CropSettings ToCropSettings() => new()
    {
        Vertical = CropVertical,
        Horizontal = CropHorizontal,
        MinPercent = CropMinPercent,
        MaxPercent = CropMaxPercent,
        BlackLevel = CropBlackLevel,
        OverlookPercent = CropOverlookPercent,
        HoldMs = CropHoldMs,
        InsetPercent = CropInsetPercent
    };

    /// <summary>
    /// Whether fixtures switched out of the painting are still drawn on the canvas.
    ///
    /// A layout is easier to read without them, but they must not disappear altogether:
    /// the selected one is always drawn, otherwise picking it from the list would point at
    /// an empty patch of canvas.
    /// </summary>
    public bool ShowDisabled { get; set; }

    /// <summary>
    /// Whether the captured frame is drawn on the canvas under the fixtures.
    ///
    /// The layout answers "which part of the screen does this LED watch" only in the
    /// abstract; with the picture itself lying under the fixtures the answer is visible
    /// directly. It is the reduced frame the painting already works from, so it costs a
    /// copy and nothing else.
    /// </summary>
    public bool ShowScreen { get; set; }

    /// <summary>Whether the canvas is shown at all, or the window is just the settings.</summary>
    public bool ShowCanvas { get; set; } = true;

    /// <summary>
    /// Keeps the monitor and every fixture in view without the button being pressed.
    ///
    /// Off by default: a canvas that re-centres itself takes the zoom away, and someone
    /// working on one corner of the case put it there on purpose.
    /// </summary>
    public bool AutoFitCanvas { get; set; }

    // ---- тест размещения --------------------------------------------------

    public TestShape TestShape { get; set; } = TestShape.Circle;

    /// <summary>Diameter or side of the test patch, in millimetres of scene.</summary>
    public double TestSizeMm { get; set; } = 150;

    public string TestColor { get; set; } = "#FF4020";

    /// <summary>
    /// Light each LED by the part of its sampling area under the patch, as a real frame
    /// would, instead of by whether the patch covers the LED itself.
    ///
    /// The two answer different questions. By the LED the test checks where the LEDs are
    /// placed; by the area it shows what each LED actually reads, which is what matters
    /// once the sampling area is wide or taken from the size of the fixture.
    /// </summary>
    public bool TestByArea { get; set; } = true;

    // ---- основное ---------------------------------------------------------

    public WindowBackdrop Backdrop { get; set; } = WindowBackdrop.MicaAlt;

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
    public bool AutoStartOpenRgb { get; set; }

    /// <summary>Empty means "find it yourself".</summary>
    public string OpenRgbPath { get; set; } = "";

    /// <summary>
    /// Language code, as a string rather than an enum: a language added as a file in the
    /// lang folder has nowhere else to be written down.
    /// </summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Asks GitHub at startup whether a newer release exists. Off by default: it is the
    /// only thing here that reaches outside the machine, and that is not a thing to start
    /// doing without being asked.
    /// </summary>
    public bool CheckUpdates { get; set; }

    /// <summary>
    /// Only worth it for the SMBus, which is to say for DRAM. Everything else - the
    /// motherboard controller, the graphics card - is reachable without elevation, and
    /// staying unelevated means no UAC prompt at every login.
    /// </summary>
    public bool OpenRgbAsAdmin { get; set; }

    /// <summary>
    /// The plugins switched on, by the name of their folder under the plugins folder. Off
    /// until switched on: starting a plugin runs its code.
    ///
    /// An array replaced whole on every change, never edited in place: <see cref="CopyFrom"/>
    /// copies properties by reference, and a list edited in place would change the applied
    /// copy behind Cancel along with the live one.
    /// </summary>
    public string[] Plugins { get; set; } = [];

    // ---- питание ----------------------------------------------------------

    public bool OffOnExit { get; set; } = true;
    public bool OffOnDisplayOff { get; set; } = true;
    public bool OffOnLock { get; set; } = true;
    public bool OffOnSuspend { get; set; } = true;

    /// <summary>
    /// How long to leave the hardware alone after a wake.
    ///
    /// It began as a guard for our own writes: OpenRGB was seen dying 41 seconds after a
    /// resume with an access violation, because its USB devices are re-enumerated while it
    /// still holds the old handles. With the server now rescanned or restarted rather than
    /// kept, that job has moved - the pause holds the rescan or restart back instead,
    /// because a server that goes looking for hardware over a bus that is still settling
    /// comes back with half a device list, or does not come back at all.
    ///
    /// In <see cref="WakeRecovery.Nothing"/> it keeps its original meaning, since there
    /// the old server carries on with the handles it had.
    /// </summary>
    public int ResumeDelayMs { get; set; } = 2000;

    /// <summary>
    /// Rescan first, restart if that fails. With frames addressed by id, OpenRGB 1.0 brought
    /// the lighting back after sleep with a rescan of a few seconds; an older server cannot
    /// rescan, so it goes straight to the restart it would have had anyway.
    /// </summary>
    public WakeRecovery WakeRecovery { get; set; } = WakeRecovery.RescanThenRestart;

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
        Converters = { new CaptureSourceConverter(), new WindowBackdropConverter(), new JsonStringEnumConverter() }
    };

    [JsonIgnore]
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaseLight");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(Folder, "config.json");

    /// <summary>
    /// The name the settings had before, kept because a file of hand-placed fixtures is
    /// worth hours at the case and must not be lost to a rename.
    /// </summary>
    [JsonIgnore]
    public static string LegacyPath => Path.Combine(Folder, "scene.json");

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
        if (path == null) MigrateFromScene();
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

    /// <summary>
    /// Set when the migration failed, reported once the log destination is known. Load()
    /// runs from a field initialiser, before the log has been pointed at the settings
    /// folder, so logging here would leave a stray file next to the executable.
    /// </summary>
    [JsonIgnore]
    public static string? MigrationNote { get; private set; }

    /// <summary>
    /// Carries the settings over from the old file name, once and without saying so.
    ///
    /// A copy rather than a move: the old file costs nothing where it lies, and leaving it
    /// means a version of the program from before the rename still finds its settings.
    /// Success is silent because nothing was asked - from the user's side the settings are
    /// simply where they were.
    /// </summary>
    static void MigrateFromScene()
    {
        try
        {
            if (File.Exists(DefaultPath) || !File.Exists(LegacyPath)) return;

            Directory.CreateDirectory(Folder);
            File.Copy(LegacyPath, DefaultPath);
        }
        catch (Exception ex)
        {
            MigrationNote = "не удалось перенести scene.json: " + ex.Message;
        }
    }

    /// <summary>Throws on a bad file, so import can tell the user what went wrong.</summary>
    public static Scene Import(string path) =>
        JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException(Loc.P("файл пуст или не содержит настроек", "the file is empty or holds no settings"));

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

    // ---- умолчания --------------------------------------------------------

    /// <summary>
    /// What a reset leaves alone: everything that describes this particular machine rather
    /// than a preference.
    ///
    /// The fixtures and the monitor rectangle are the layout itself, measured against the
    /// case with a ruler; the screen, the language and the plugins are what the installation
    /// is, and switching a plugin off would leave its fixtures without a device; the
    /// window geometry is not edited by hand at all and is written on the way out. Every
    /// other setting is reset by name lookup rather than from a list, so one added later is
    /// covered without anyone having to remember this method.
    /// </summary>
    static readonly string[] Preserved =
    {
        nameof(Fixtures), nameof(Monitor),
        nameof(MonitorDeviceName), nameof(MonitorModel),
        nameof(Language), nameof(Plugins),
        nameof(WindowWidth), nameof(WindowHeight), nameof(WindowLeft), nameof(WindowTop),
        nameof(WindowMaximized)
    };

    /// <summary>
    /// Puts every setting back to its shipped value, keeping the layout and everything else
    /// in <see cref="Preserved"/>.
    ///
    /// Applied live like any other edit rather than written straight to disk: a reset is a
    /// large change to look at, and Cancel has to be able to take it back.
    /// </summary>
    public void ResetToDefaults()
    {
        var shipped = new Scene();

        // размеры фигур сохраняются, поэтому их смысл возвращается к обычному вместе с флагом
        SetSampleBySize(shipped.SampleBySize);

        foreach (var prop in typeof(Scene).GetProperties())
            if (prop.CanRead && prop.CanWrite && Array.IndexOf(Preserved, prop.Name) < 0)
                prop.SetValue(this, prop.GetValue(shipped));
    }
}
