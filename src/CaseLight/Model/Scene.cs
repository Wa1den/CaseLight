using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaseLight.Model;

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
}

/// <summary>
/// Everything that lights up, laid out in millimetres on one plane seen from the front.
///
/// Millimetres rather than pixels or arbitrary units: the mapping from screen to case is
/// a physical question - how far from the monitor a fan actually stands decides which part
/// of the picture it should echo.
/// </summary>
public sealed class Scene
{
    public MonitorPlacement Monitor { get; set; } = new();
    public List<Fixture> Fixtures { get; set; } = new();

    // ---- раскраска --------------------------------------------------------

    /// <summary>
    /// How much of the screen one LED averages over, in millimetres of scene.
    ///
    /// A single pixel would make the case flicker on every small movement; too wide and
    /// everything turns into the same brown average. Roughly the size of the lit object
    /// itself works well.
    /// </summary>
    public double SampleRadiusMm { get; set; } = 60;

    public int MaxFps { get; set; } = 30;

    public double Brightness { get; set; } = 1.0;
    public double Gamma { get; set; } = 2.2;
    public double Saturation { get; set; } = 1.15;
    public double MinLuma { get; set; }
    public int TemperatureK { get; set; } = 6500;

    /// <summary>Light rises quickly and falls gently, as in Ambilight.</summary>
    public double SmoothingRise { get; set; } = 0.55;
    public double SmoothingFall { get; set; } = 0.18;

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaseLight", "scene.json");

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
}
