using System;
using System.Collections.Generic;
using System.Linq;
using CaseLight.Plugins;

namespace CaseLight.Audio;

/// <summary>
/// The volume of the default output as a bar across chosen rows, shown for a while after it
/// changes. The spectrum went over to the equalizer for fixtures, which covers the keyboard
/// as well.
/// </summary>
public sealed class AudioEffect : ILightEffect
{
    /// <summary>How long the bar takes to fade after its time is up.</summary>
    const double FadeSeconds = 0.3;

    readonly AudioSource _source = new();

    // настройки: пишутся в Configure, читаются в Paint, оба из потока эффектов
    bool _always;
    int[] _rows = [];
    LightColor _fill, _track, _mute;
    double _holdSeconds;

    bool _configured;
    long _previewTicks;
    string _settings = "";

    static readonly string[] Keys = ["vol.rows", "vol.fill", "vol.track", "vol.mute", "vol.hold", "vol.always"];

    static string T(string ru, string en) => PluginApi.Language == "ru" ? ru : en;

    public int ApiVersion => PluginApi.Version;
    public string Name => T("Шкала громкости", "Volume bar");
    public string Description => T(
        "Уровень громкости полосой по рядам клавиш.",
        "The volume level as a bar along rows of keys.");
    public string Icon => "";

    public IReadOnlyList<EffectSetting> Settings =>
    [
        new("volume", SettingKind.Header, T("Громкость", "Volume"))
        {
            Help = T("Шкала громкости устройства вывода по умолчанию. Появляется при изменении громкости или выключении звука и гаснет через заданное время.",
                     "The volume of the default output device. It appears when the volume changes or the sound is muted, and goes out after the time set.")
        },
        new("vol.rows", SettingKind.Rows, T("Ряды шкалы", "Rows of the bar"))
        {
            Default = "1",
            Help = T("Ряды считаются сверху. Шкала заполняет каждый отмеченный ряд слева направо.",
                     "Rows are counted from the top. The bar fills each ticked row from left to right.")
        },
        new("vol.fill", SettingKind.Color, T("Цвет шкалы", "Bar colour")) { Default = "#20C0FF" },
        new("vol.track", SettingKind.Color, T("Цвет незаполненной части", "Colour of the empty part"))
        {
            Default = "#101010",
            Help = T("Чёрный оставляет незаполненную часть тёмной, картинка с экрана под ней не видна.",
                     "Black leaves the empty part dark; the picture from the screen does not show under it.")
        },
        new("vol.mute", SettingKind.Color, T("Цвет при выключенном звуке", "Colour when muted")) { Default = "#FF2020" },
        new("vol.hold", SettingKind.Slider, T("Время показа", "Shown for"))
        {
            Default = "2", Min = 0.5, Max = 10, Step = 0.5, Unit = T(" с", " s")
        },
        new("vol.always", SettingKind.Toggle, T("Показывать постоянно", "Always show")),
    ];

    public void Start() { }

    public void Configure(EffectValues values)
    {
        _always = values.Bool("vol.always");
        _rows = values.Ints("vol.rows");
        _fill = values.Color("vol.fill");
        _track = values.Color("vol.track");
        _mute = values.Color("vol.mute");
        _holdSeconds = Math.Max(0.1, values.Number("vol.hold"));

        // правка в разделе показывает шкалу, чтобы её было видно, пока её настраивают
        string settings = string.Join("|", Keys.Select(values.Raw));
        if (_configured && settings != _settings) _previewTicks = Environment.TickCount64;
        _settings = settings;
        _configured = true;
    }

    public bool Paint(EffectCanvas canvas)
    {
        if (!_source.Known) return false;

        double shown = 1;
        if (!_always)
        {
            long changed = Math.Max(_source.VolumeChangedTicks, _previewTicks);
            if (changed == 0) return false;

            double age = (Environment.TickCount64 - changed) / 1000.0;
            if (age >= _holdSeconds + FadeSeconds) return false;
            if (age > _holdSeconds) shown = 1 - (age - _holdSeconds) / FadeSeconds;
        }

        double level = Math.Clamp(_source.Volume, 0, 1);
        var fill = _source.Muted ? _mute : _fill;
        bool any = false;

        foreach (int r in _rows)
        {
            if (r >= canvas.Rows.Count) continue;
            var row = canvas.Rows[r];
            if (row.Length == 0) continue;

            var (lefts, widths, left, width) = Extent(canvas.Layout, row);
            double edge = left + level * width;

            for (int i = 0; i < row.Length; i++)
            {
                double covered = widths[i] <= 0 ? 0 : Math.Clamp((edge - lefts[i]) / widths[i], 0, 1);
                canvas.Blend(row[i], _track.Lerp(fill, covered), shown);
                any = true;
            }
        }

        return any;
    }

    /// <summary>Left edge and width of each LED of a row, and of the row as a whole.</summary>
    static (double[] Lefts, double[] Widths, double Left, double Width) Extent(IReadOnlyList<LedRect>? layout, int[] row)
    {
        var lefts = new double[row.Length];
        var widths = new double[row.Length];

        for (int i = 0; i < row.Length; i++)
        {
            if (layout != null)
            {
                lefts[i] = layout[row[i]].X;
                widths[i] = layout[row[i]].Width;
            }
            else
            {
                lefts[i] = i;
                widths[i] = 1;
            }
        }

        double left = lefts.Min();
        double right = lefts.Zip(widths, (l, w) => l + w).Max();
        return (lefts, widths, left, Math.Max(1e-9, right - left));
    }

    public void Dispose() => _source.Dispose();
}
