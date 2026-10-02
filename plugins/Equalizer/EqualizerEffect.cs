using System;
using System.Collections.Generic;
using System.Linq;
using CaseLight.Audio;
using CaseLight.Plugins;

namespace CaseLight.Equalizer;

/// <summary>
/// The spectrum of what the default output plays, laid over fixtures of the plan where they
/// stand: the columns are drawn across the area the fixtures take up on the canvas, and each
/// LED shows the part of the picture its own area falls on.
/// </summary>
public sealed class EqualizerEffect : ILightEffect
{
    /// <summary>
    /// How long the spectrum counts as showing after the sound stops, so that the picture
    /// under it does not come back for a moment in every pause between songs.
    /// </summary>
    const int SilenceHoldMs = 2000;

    /// <summary>Sound older than this is taken as silence: loopback gives no data while nothing plays.</summary>
    const int StaleSoundMs = 200;

    /// <summary>An area narrower than this, in millimetres, has no width to lay bands or a level along.</summary>
    const double FlatMm = 1;

    readonly AudioSource _source = new();
    readonly Spectrum _spectrum = new();
    readonly MediaWatch _media = new();

    // настройки: пишутся в Configure, читаются в Paint, одно с другим не пересекается
    bool _mediaOnly, _eachOwn;
    int _direction, _bands, _under, _cycle;
    LightColor _low, _high;
    double _brightness, _gainDb, _rangeDb, _fallSeconds, _cycleSeconds;

    double[] _target = [], _shown = [];
    double _lastSeconds;

    // цвета шкалы на текущий кадр: пишутся в начале Paint, читаются в ScaleColour
    LightColor _frameLow, _frameHigh;
    double _rainbowShift;

    /// <summary>Values of the <c>eq.cycle</c> choice.</summary>
    const int CycleNone = 0, CycleChosen = 1, CycleRainbow = 2, CycleDynamic = 3;

    /// <summary>Share of the time of a pair in <see cref="CycleDynamic"/> that it holds before it starts turning into the next.</summary>
    const double PairHold = 0.6;

    /// <summary>
    /// Pairs of the dynamic cycle, start and end of the scale, in the order they follow
    /// each other.
    ///
    /// The ends of a pair are 60 to 135 degrees apart in hue, from neighbouring colours to
    /// nearly a triad. Opposite colours would make a contrast at the ends, but the middle of
    /// the scale between them passes near grey and looks dull on an LED. Every pair passes
    /// through saturated hues on its way, and next pairs share a part of the circle, so the
    /// change from one to the next does not go through white.
    /// </summary>
    static readonly (LightColor Low, LightColor High)[] Pairs =
    [
        (LightColor.Parse("#00FF60"), LightColor.Parse("#FF2000")),  // зелёный — красный через жёлтый, как у индикатора уровня
        (LightColor.Parse("#FF0050"), LightColor.Parse("#FFB000")),  // малиновый — янтарный, соседние тёплые
        (LightColor.Parse("#FFC000"), LightColor.Parse("#FF00A0")),  // золотой — пурпурный через красный
        (LightColor.Parse("#8000FF"), LightColor.Parse("#FF3080")),  // фиолетовый — розовый, соседние
        (LightColor.Parse("#00D0FF"), LightColor.Parse("#FF00C0")),  // голубой — пурпурный через синий, почти триада
        (LightColor.Parse("#00FFA0"), LightColor.Parse("#2040FF")),  // аквамарин — синий, соседние холодные
        (LightColor.Parse("#A0FF00"), LightColor.Parse("#00E0FF")),  // салатовый — бирюзовый через зелёный
    ];

    static string T(string ru, string en) => PluginApi.Language == "ru" ? ru : en;

    public int ApiVersion => PluginApi.Version;
    public string Name => T("Эквалайзер", "Equalizer");
    public string Description => T(
        "Спектр звука на выбранных фигурах плана, по их положению на холсте.",
        "The spectrum of the sound on the fixtures chosen, laid out by where they stand on the canvas.");
    public string Icon => "\uE9E9";
    public EffectTarget Target => EffectTarget.Fixtures;

    public IReadOnlyList<EffectSetting> Settings =>
    [
        new("eq", SettingKind.Header, T("Спектр", "Spectrum"))
        {
            Help = T("Спектр того, что играет устройство вывода по умолчанию, разложенный по площади выбранных фигур на холсте. Каждый диод берёт тот участок спектра, на который приходится его зона на плане.",
                     "The spectrum of what the default output device plays, spread over the area of the fixtures chosen on the canvas. Each LED takes the part of the spectrum its area on the plan falls on.")
        },
        new("eq.media", SettingKind.Toggle, T("Только когда играет медиа", "Only while media plays"))
        {
            Help = T("Спектр показывается, пока плеер или браузер что-то играет: то, что Windows показывает в панели мультимедиа у громкости. Звуки системы, игр и голосовых чатов его не включают.",
                     "The spectrum shows while a player or a browser plays something: what Windows shows in the media panel next to the volume. Sounds of the system, games and voice chats do not bring it on.")
        },
        new("eq.area", SettingKind.Choice, T("Область", "Area"))
        {
            Default = "0",
            Options = [T("Общая для всех выбранных фигур", "One for all the fixtures chosen"), T("Своя у каждой фигуры", "Each fixture its own")],
            Help = T("Общая область — прямоугольник, в который входят все выбранные фигуры: три вентилятора в ряд показывают один широкий спектр. Со своей областью каждая фигура показывает спектр целиком.",
                     "One area is the rectangle all the fixtures chosen fit into: three fans in a row show one wide spectrum. With an area each, every fixture shows the whole spectrum.")
        },
        new("eq.direction", SettingKind.Choice, T("Направление", "Direction"))
        {
            Default = "0",
            Options =
            [
                T("Частоты слева направо, уровень снизу вверх", "Frequencies left to right, level upwards"),
                T("Частоты снизу вверх, уровень слева направо", "Frequencies upwards, level left to right"),
                T("Общий уровень вдоль длинной стороны", "Overall level along the long side")
            ],
            Help = T("Для вертикальной ленты подходит общий уровень или частоты снизу вверх: по ширине ленты полосы не разложить.",
                     "A vertical strip suits the overall level or frequencies upwards: its width has no room for bands.")
        },
        new("eq.bands", SettingKind.Slider, T("Число полос", "Bands"))
        {
            Default = "8", Min = 1, Max = 32, Step = 1,
            Help = T("Полосы делят область поровну; диод попадает в полосу по центру своей зоны.",
                     "The bands split the area evenly; an LED falls into a band by the centre of its area.")
        },
        new("eq.low", SettingKind.Color, T("Цвет в начале шкалы", "Colour at the start of the scale")) { Default = "#00E060" },
        new("eq.high", SettingKind.Color, T("Цвет в конце шкалы", "Colour at the end of the scale")) { Default = "#FF3000" },
        new("eq.cycle", SettingKind.Choice, T("Смена цветов", "Colour cycle"))
        {
            Default = "0",
            Options = [T("Нет", "None"), T("От выбранных цветов", "From the colours chosen"), T("Радуга", "Rainbow"), T("Динамическая", "Dynamic")],
            Help = T("От выбранных цветов: оба цвета шкалы вместе проходят по кругу оттенков, разница между ними, насыщенность и яркость остаются прежними. Радуга: вдоль шкалы лежит весь круг оттенков и равномерно сдвигается по ней. Динамическая: шкала по очереди принимает пары сочетающихся цветов и плавно переходит от одной к другой. Выбранные цвета используются в первых двух вариантах.",
                     "From the colours chosen: both colours of the scale go round the colour wheel together, keeping the difference between them, their saturation and brightness. Rainbow: the whole colour wheel lies along the scale and moves evenly along it. Dynamic: the scale takes pairs of matching colours in turn and passes smoothly from one to the next. The colours chosen are used in the first two.")
        },
        new("eq.cycle.period", SettingKind.Slider, T("Период смены цветов", "Colour cycle period"))
        {
            Default = "10", Min = 1, Max = 30, Step = 1, Unit = T(" с", " s"),
            Help = T("От выбранных цветов и в радуге — время полного круга оттенков, в динамической смене — время одной пары вместе с переходом к следующей.",
                     "From the colours chosen and in the rainbow, the time of the whole way round the colour wheel; in the dynamic cycle, the time of one pair along with the change to the next.")
        },
        new("eq.under", SettingKind.Choice, T("Под спектром", "Under the spectrum"))
        {
            Default = "1",
            Options = [T("Картинка с экрана", "Picture from the screen"), T("Приглушённая картинка", "Dimmed picture"), T("Темнота", "Darkness")]
        },
        new("eq.brightness", SettingKind.Slider, T("Яркость", "Brightness"))
        {
            Default = "100", Min = 5, Max = 100, Step = 5, Unit = " %",
            Help = T("Спектр рисуется после яркости фигуры и её не наследует, поэтому яркость у него своя.",
                     "The spectrum is drawn after the brightness of a fixture and does not take it on, so it has a brightness of its own.")
        },
        new("eq.gain", SettingKind.Slider, T("Усиление", "Gain"))
        {
            Default = "0", Min = -20, Max = 30, Step = 1, Unit = T(" дБ", " dB"),
            Help = T("Поднимает все полосы. Нужно, когда при тихой музыке спектр едва виден.",
                     "Raises every band. For quiet music whose spectrum barely shows.")
        },
        new("eq.range", SettingKind.Slider, T("Диапазон", "Range"))
        {
            Default = "50", Min = 20, Max = 90, Step = 5, Unit = T(" дБ", " dB"),
            Help = T("Насколько тише полной громкости звук ещё виден. Больший диапазон показывает тихие звуки, но уровень меньше меняется.",
                     "How far below full scale a sound still shows. A wider range shows quiet sounds, but the level moves less.")
        },
        new("eq.fall", SettingKind.Slider, T("Время спада", "Fall time"))
        {
            Default = "0.5", Min = 0.1, Max = 2, Step = 0.1, Unit = T(" с", " s"),
            Help = T("За сколько уровень опускается с полного до нуля. Поднимается он сразу.",
                     "How long the level takes to fall from full to zero. It rises at once.")
        },
    ];

    public void Start() { }

    public void Configure(EffectValues values)
    {
        _mediaOnly = values.Bool("eq.media");
        _eachOwn = values.Int("eq.area") == 1;
        _direction = Math.Clamp(values.Int("eq.direction"), 0, 2);
        _bands = Math.Clamp(values.Int("eq.bands"), 1, 64);
        _low = values.Color("eq.low");
        _high = values.Color("eq.high");
        _cycle = Math.Clamp(values.Int("eq.cycle"), CycleNone, CycleDynamic);
        _cycleSeconds = Math.Clamp(values.Number("eq.cycle.period"), 1, 30);
        _under = values.Int("eq.under");
        _brightness = Math.Clamp(values.Number("eq.brightness") / 100, 0, 1);
        _gainDb = values.Number("eq.gain");
        _rangeDb = Math.Max(1, values.Number("eq.range"));
        _fallSeconds = Math.Max(0.01, values.Number("eq.fall"));

        if (_target.Length != _bands)
        {
            _target = new double[_bands];
            _shown = new double[_bands];
        }
    }

    public bool Paint(EffectCanvas canvas)
    {
        double dt = Math.Clamp(canvas.Seconds - _lastSeconds, 0, 0.1);
        _lastSeconds = canvas.Seconds;

        if (!Measure(dt) || canvas.Layout is not { } layout) return false;

        double overall = _shown.Max();

        PickColours(canvas.Seconds);

        if (_eachOwn)
            foreach (var part in canvas.Parts) PaintArea(canvas, layout, part, overall);
        else
            PaintArea(canvas, layout, Enumerable.Range(0, canvas.LedCount).ToArray(), overall);

        return true;
    }

    /// <summary>Brings the levels of the bands up to date.</summary>
    /// <returns>Whether there is anything to show.</returns>
    bool Measure(double dt)
    {
        long now = Environment.TickCount64;

        // Без медиа звук не снимается вовсе: уровень опускается, как в тишине.
        bool media = true;
        if (_mediaOnly)
        {
            _media.Want();
            media = !_media.Available || _media.Playing;
        }

        if (media) _source.WantCapture();
        bool sound = media && now - _source.LastSoundTicks < StaleSoundMs;

        if (sound) _spectrum.Measure(_source, _target, _gainDb, _rangeDb);
        else Array.Clear(_target);

        for (int b = 0; b < _bands; b++)
            _shown[b] = _target[b] >= _shown[b] ? _target[b] : Math.Max(_target[b], _shown[b] - dt / _fallSeconds);

        // Когда долго тихо, спектр не рисуется: подложка «Под спектром» гасила бы картинку и без него.
        return media && now - _source.LastSoundTicks < SilenceHoldMs || _shown.Any(v => v > 0.01);
    }

    /// <summary>Sets the colours of the scale for the frame at <paramref name="seconds"/>.</summary>
    void PickColours(double seconds)
    {
        double step = seconds / _cycleSeconds, turn = step % 1;
        (_frameLow, _frameHigh) = (_low, _high);

        switch (_cycle)
        {
            case CycleChosen:
                (_frameLow, _frameHigh) = (Rotate(_low, turn * 360), Rotate(_high, turn * 360));
                break;

            case CycleRainbow:
                _rainbowShift = turn * 360;
                break;

            case CycleDynamic:
                int pair = (int)(step % Pairs.Length);
                var (from, to) = (Pairs[pair], Pairs[(pair + 1) % Pairs.Length]);

                // пара стоит PairHold своего времени, остальное время переходит в следующую
                double t = Math.Clamp((turn - PairHold) / (1 - PairHold), 0, 1);
                t = t * t * (3 - 2 * t);
                (_frameLow, _frameHigh) = (Blend(from.Low, to.Low, t), Blend(from.High, to.High, t));
                break;
        }
    }

    /// <summary>The colour of the scale at <paramref name="t"/>, from its start at 0 to its end at 1.</summary>
    LightColor ScaleColour(double t) =>
        _cycle == CycleRainbow
            ? Rotate(new LightColor(255, 0, 0), Math.Clamp(t, 0, 1) * 360 - _rainbowShift)
            : Blend(_frameLow, _frameHigh, t);

    /// <summary>Lays the spectrum over one area: the rectangle the areas of these LEDs take up on the plan.</summary>
    void PaintArea(EffectCanvas canvas, IReadOnlyList<LedRect> layout, int[] leds, double overall)
    {
        if (leds.Length == 0) return;

        double left = leds.Min(i => layout[i].X), right = leds.Max(i => layout[i].X + layout[i].Width);
        double top = leds.Min(i => layout[i].Y), bottom = leds.Max(i => layout[i].Y + layout[i].Height);
        double width = right - left, height = bottom - top;

        // общий уровень ложится вдоль длинной стороны области
        bool levelUp = _direction switch
        {
            0 => true,
            1 => false,
            _ => height >= width
        };

        foreach (int led in leds)
        {
            var r = layout[led];

            // где диод по оси частот (0..1) и какую часть оси уровня он занимает
            double across, from, to;
            if (levelUp)
            {
                across = width < FlatMm ? 0.5 : (r.X + r.Width / 2 - left) / width;
                (from, to) = height < FlatMm ? (0.0, 1.0) : ((bottom - r.Y - r.Height) / height, (bottom - r.Y) / height);
            }
            else
            {
                across = height < FlatMm ? 0.5 : (bottom - r.Y - r.Height / 2) / height;
                (from, to) = width < FlatMm ? (0.0, 1.0) : ((r.X - left) / width, (r.X + r.Width - left) / width);
            }

            double level = _direction == 2 ? overall
                : _shown[Math.Clamp((int)(across * _bands), 0, _bands - 1)];

            double lit = to - from > 1e-9 ? Math.Clamp((level - from) / (to - from), 0, 1) : level > from ? 1 : 0;
            var colour = ScaleColour((from + to) / 2).Scale(_brightness);

            var under = _under switch
            {
                0 => canvas.Get(led),
                1 => canvas.Get(led).Scale(0.25),
                _ => LightColor.Black
            };

            canvas.Set(led, under.Lerp(colour, lit));
        }
    }

    /// <summary>Exponent between the byte an LED is driven with and how bright it looks.</summary>
    const double Perceived = 2.2;

    /// <summary>
    /// The colour at <paramref name="t"/> of the scale from <paramref name="from"/> to
    /// <paramref name="to"/>, even to the eye.
    ///
    /// An LED gives light in proportion to its byte, and the eye tells dim light apart far
    /// better than bright. Mixed by bytes, the channel the end colour adds was plain long
    /// before the middle: from violet to turquoise the green took over at about a fifth of
    /// the scale. Mixed after the exponent, the middle of the scale looks like the middle.
    /// </summary>
    static LightColor Blend(LightColor from, LightColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);

        static byte Channel(byte a, byte b, double t)
        {
            double x = Math.Pow(a / 255.0, 1 / Perceived), y = Math.Pow(b / 255.0, 1 / Perceived);
            return (byte)Math.Round(Math.Pow(x + (y - x) * t, Perceived) * 255);
        }

        return new LightColor(Channel(from.R, to.R, t), Channel(from.G, to.G, t), Channel(from.B, to.B, t));
    }

    /// <summary>
    /// The colour turned by <paramref name="degrees"/> round the hue circle, its saturation
    /// and value kept.
    ///
    /// The circle is laid out after the exponent, as in <see cref="Blend"/>. Laid out by
    /// bytes, ten degrees from red already gave blue a byte of 42, which the eye sees at
    /// 44 % of full, and the red of the rainbow turned pink almost at once.
    /// </summary>
    static LightColor Rotate(LightColor c, double degrees)
    {
        static double Eye(byte v) => Math.Pow(v / 255.0, 1 / Perceived);
        double r = Eye(c.R), g = Eye(c.G), b = Eye(c.B);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
        if (chroma <= 0) return c;

        double hue = max == r ? (g - b) / chroma
                   : max == g ? (b - r) / chroma + 2
                   : (r - g) / chroma + 4;
        hue = ((hue * 60 + degrees) % 360 + 360) % 360 / 60;

        double x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (r1, g1, b1) = (int)hue switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };

        static byte Byte(double v) => (byte)Math.Round(Math.Pow(Math.Clamp(v, 0, 1), Perceived) * 255);
        return new LightColor(Byte(r1 + min), Byte(g1 + min), Byte(b1 + min));
    }

    public void Dispose()
    {
        _source.Dispose();
        _media.Dispose();
    }
}
