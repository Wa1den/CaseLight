using System;
using System.Collections.Generic;
using System.Linq;
using CaseLight.Plugins;

namespace CaseLight.Audio;

/// <summary>
/// The volume of the default output as a bar across chosen rows, shown for a while after it
/// changes, and the spectrum of what it plays as columns across the keyboard.
/// </summary>
public sealed class AudioEffect : ILightEffect
{
    /// <summary>How long the bar takes to fade after its time is up.</summary>
    const double FadeSeconds = 0.3;

    /// <summary>
    /// How long the spectrum counts as showing after the sound stops. Without it a device
    /// with no picture under it went back to its own effect in every pause between songs
    /// and took about 1.6 s to come back.
    /// </summary>
    const int SilenceHoldMs = 2000;

    /// <summary>Sound older than this is taken as silence: loopback gives no data while nothing plays.</summary>
    const int StaleSoundMs = 200;

    readonly AudioSource _source = new();
    readonly Spectrum _spectrum = new();
    readonly MediaWatch _media = new();

    // настройки: пишутся в Configure, читаются в Paint, оба из потока эффектов
    bool _volumeOn, _volumeAlways, _eqOn, _eqMedia;
    int[] _volumeRows = [], _eqRows = [];
    LightColor _fill, _track, _mute, _low, _high;
    double _holdSeconds;
    int _bands, _under;
    double _gainDb, _rangeDb, _fallSeconds;

    bool _configured;
    long _previewTicks;
    string _volumeSettings = "";

    double[] _target = [], _shown = [];
    double _lastSeconds;
    double[] _across = [];
    IReadOnlyList<LedRect>? _acrossFor;
    int _acrossCount = -1;

    static readonly string[] VolumeKeys = ["vol.on", "vol.rows", "vol.fill", "vol.track", "vol.mute", "vol.hold", "vol.always"];

    static string T(string ru, string en) => PluginApi.Language == "ru" ? ru : en;

    public int ApiVersion => PluginApi.Version;
    public string Name => T("Громкость и эквалайзер клавиатуры", "Keyboard volume & equalizer");
    public string Description => T(
        "Уровень громкости полосой по рядам клавиш и спектр звука столбцами по клавиатуре.",
        "The volume level as a bar along rows of keys, and the spectrum of the sound as columns across the keyboard.");
    public string Icon => "\uE767";

    public IReadOnlyList<EffectSetting> Settings =>
    [
        new("volume", SettingKind.Header, T("Громкость", "Volume"))
        {
            Help = T("Шкала громкости устройства вывода по умолчанию. Появляется при изменении громкости или выключении звука и гаснет через заданное время.",
                     "The volume of the default output device. It appears when the volume changes or the sound is muted, and goes out after the time set.")
        },
        new("vol.on", SettingKind.Toggle, T("Показывать громкость", "Show the volume")) { Default = "1" },
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

        new("eq", SettingKind.Header, T("Эквалайзер", "Equalizer"))
        {
            Help = T("Спектр того, что играет устройство вывода по умолчанию: низкие частоты слева, высокие справа. Высота столбца — число отмеченных рядов.",
                     "The spectrum of what the default output device plays: low frequencies on the left, high on the right. The height of a column is the number of rows ticked.")
        },
        new("eq.on", SettingKind.Toggle, T("Показывать спектр", "Show the spectrum")) { Default = "1" },
        new("eq.media", SettingKind.Toggle, T("Только когда играет медиа", "Only while media plays"))
        {
            Help = T("Спектр показывается, пока плеер или браузер что-то играет: то, что Windows показывает в панели мультимедиа у громкости. Звуки системы, игр и голосовых чатов его не включают.",
                     "The spectrum shows while a player or a browser plays something: what Windows shows in the media panel next to the volume. Sounds of the system, games and voice chats do not bring it on.")
        },
        new("eq.rows", SettingKind.Rows, T("Ряды спектра", "Rows of the spectrum")) { Default = "0,1,2,3,4,5,6,7,8,9" },
        new("eq.bands", SettingKind.Slider, T("Число полос", "Bands"))
        {
            Default = "16", Min = 4, Max = 32, Step = 1,
            Help = T("Полосы делят ширину клавиатуры поровну; клавиша попадает в полосу по своему центру. На раскладке 75 % в ряду 16 клавиш.",
                     "The bands split the width of the keyboard evenly; a key falls into a band by its centre. A 75 % layout has 16 keys in a row.")
        },
        new("eq.low", SettingKind.Color, T("Цвет внизу", "Colour at the bottom")) { Default = "#00E060" },
        new("eq.high", SettingKind.Color, T("Цвет вверху", "Colour at the top")) { Default = "#FF3000" },
        new("eq.under", SettingKind.Choice, T("Под столбцами", "Under the columns"))
        {
            Default = "1",
            Options = [T("Картинка с экрана", "Picture from the screen"), T("Приглушённая картинка", "Dimmed picture"), T("Темнота", "Darkness")]
        },
        new("eq.gain", SettingKind.Slider, T("Усиление", "Gain"))
        {
            Default = "0", Min = -20, Max = 30, Step = 1, Unit = T(" дБ", " dB"),
            Help = T("Поднимает все полосы. Нужно, когда при тихой музыке столбцы едва видны; при громкой и большом усилении они не опускаются ниже верхнего ряда.",
                     "Raises every band. For quiet music whose columns barely show; with loud music and much gain they stay at the top.")
        },
        new("eq.range", SettingKind.Slider, T("Диапазон", "Range"))
        {
            Default = "50", Min = 20, Max = 90, Step = 5, Unit = T(" дБ", " dB"),
            Help = T("Насколько тише полной громкости звук ещё виден. Больший диапазон показывает тихие звуки, но столбцы меньше меняются.",
                     "How far below full scale a sound still shows. A wider range shows quiet sounds, but the columns move less.")
        },
        new("eq.fall", SettingKind.Slider, T("Время спада", "Fall time"))
        {
            Default = "0.5", Min = 0.1, Max = 2, Step = 0.1, Unit = T(" с", " s"),
            Help = T("За сколько столбец опускается с полной высоты до нуля. Поднимается он сразу.",
                     "How long a column takes to fall from full height to zero. It rises at once.")
        },
    ];

    public void Start() { }

    public void Configure(EffectValues values)
    {
        _volumeOn = values.Bool("vol.on");
        _volumeAlways = values.Bool("vol.always");
        _volumeRows = values.Ints("vol.rows");
        _fill = values.Color("vol.fill");
        _track = values.Color("vol.track");
        _mute = values.Color("vol.mute");
        _holdSeconds = Math.Max(0.1, values.Number("vol.hold"));

        _eqOn = values.Bool("eq.on");
        _eqMedia = values.Bool("eq.media");
        _eqRows = values.Ints("eq.rows");
        _bands = Math.Clamp(values.Int("eq.bands"), 1, 64);
        _low = values.Color("eq.low");
        _high = values.Color("eq.high");
        _under = values.Int("eq.under");
        _gainDb = values.Number("eq.gain");
        _rangeDb = Math.Max(1, values.Number("eq.range"));
        _fallSeconds = Math.Max(0.01, values.Number("eq.fall"));

        if (_target.Length != _bands)
        {
            _target = new double[_bands];
            _shown = new double[_bands];
        }

        // Правка шкалы показывает её, чтобы шкалу было видно, пока её настраивают. Правки
        // спектра шкалу не зажигают: на клавиатуре она нужна только при смене громкости.
        string volumeSettings = string.Join("|", VolumeKeys.Select(values.Raw));
        if (_configured && volumeSettings != _volumeSettings) _previewTicks = Environment.TickCount64;
        _volumeSettings = volumeSettings;
        _configured = true;
    }

    public bool Paint(EffectCanvas canvas)
    {
        double dt = Math.Clamp(canvas.Seconds - _lastSeconds, 0, 0.1);
        _lastSeconds = canvas.Seconds;

        bool drew = false;
        if (_eqOn) drew |= PaintSpectrum(canvas, dt);
        if (_volumeOn) drew |= PaintVolume(canvas);
        return drew;
    }

    bool PaintVolume(EffectCanvas canvas)
    {
        if (!_source.Known) return false;

        double shown = 1;
        if (!_volumeAlways)
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

        foreach (int r in _volumeRows)
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

    bool PaintSpectrum(EffectCanvas canvas, double dt)
    {
        var rows = _eqRows.Where(r => r < canvas.Rows.Count).ToArray();
        if (rows.Length == 0) return false;

        long now = Environment.TickCount64;

        // Без медиа звук не снимается вовсе: столбцы опускаются, как в тишине.
        bool media = true;
        if (_eqMedia)
        {
            _media.Want();
            // Без удержания: после паузы плеера спектр показывал бы остальной звук, голос созвона.
            media = !_media.Available || _media.Playing;
        }

        if (media) _source.WantCapture();
        bool sound = media && now - _source.LastSoundTicks < StaleSoundMs;

        if (sound) _spectrum.Measure(_source, _target, _gainDb, _rangeDb);
        else Array.Clear(_target);

        for (int b = 0; b < _bands; b++)
            _shown[b] = _target[b] >= _shown[b] ? _target[b] : Math.Max(_target[b], _shown[b] - dt / _fallSeconds);

        // Когда долго тихо или медиа не играет, спектр не рисуется вовсе: подложка «Под столбцами»
        // гасила бы картинку с экрана и без столбцов, а устройство без картинки отпускается.
        bool active = media && now - _source.LastSoundTicks < SilenceHoldMs || _shown.Any(v => v > 0.01);
        if (!active) return false;

        var across = Across(canvas);
        int k = rows.Length;

        for (int p = 0; p < k; p++)
        {
            int fromBottom = k - 1 - p;
            var colour = _low.Lerp(_high, k == 1 ? 0 : (double)fromBottom / (k - 1));

            foreach (int led in canvas.Rows[rows[p]])
            {
                int band = Math.Clamp((int)(across[led] * _bands), 0, _bands - 1);
                double lit = Math.Clamp(_shown[band] * k - fromBottom, 0, 1);

                var under = _under switch
                {
                    0 => canvas.Get(led),
                    1 => canvas.Get(led).Scale(0.25),
                    _ => LightColor.Black
                };

                canvas.Set(led, under.Lerp(colour, lit));
            }
        }

        return true;
    }

    double[] Across(EffectCanvas canvas)
    {
        if (!ReferenceEquals(_acrossFor, canvas.Layout) || _acrossCount != canvas.LedCount)
        {
            _across = LedGrid.Across(canvas.Layout, canvas.LedCount);
            _acrossFor = canvas.Layout;
            _acrossCount = canvas.LedCount;
        }

        return _across;
    }

    public void Dispose()
    {
        _source.Dispose();
        _media.Dispose();
    }
}
