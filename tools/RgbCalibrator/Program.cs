using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenRGB.NET;
using RgbColor = OpenRGB.NET.Color;

namespace RgbCalibrator;

/// <summary>
/// Finds out how many LEDs are really on an addressable header, by hand and by eye.
///
/// Nothing can be asked of the hardware here: an ARGB header has no idea how long the
/// strip plugged into it is, and OpenRGB just guesses 60. Fans make it worse - the LEDs
/// run around a ring and several fans daisy-chain into one header, so counting them from
/// a static pattern is hopeless.
///
/// So the answer is found by walking: press plus until the lit run stops growing. That
/// point is the real length.
/// </summary>
static class Program
{
    static OpenRgbClient? _client;
    static Device[] _devices = Array.Empty<Device>();

    static ComboBox _deviceBox = null!;
    static ComboBox _zoneBox = null!;
    static TextBox _lengthBox = null!;
    static TextBlock _indexText = null!;
    static TextBlock _detailText = null!;
    static TextBlock _statusText = null!;
    static TextBlock _limitText = null!;

    static RadioButton _modeSingle = null!;
    static RadioButton _modeGrow = null!;
    static RadioButton _modeMarks = null!;

    static int _index;
    static RgbColor _paint = new(255, 255, 255);

    [STAThread]
    static void Main()
    {
        var app = new Application();
        var window = BuildWindow();

        window.Loaded += (_, _) => Connect();
        window.Closed += (_, _) => _client?.Dispose();

        app.Run(window);
    }

    // ---- окно ------------------------------------------------------------

    static Window BuildWindow()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        // выбор устройства и зоны
        _deviceBox = new ComboBox { MinWidth = 320, Margin = new Thickness(0, 0, 8, 0) };
        _deviceBox.SelectionChanged += (_, _) => OnDeviceChanged();

        _zoneBox = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
        _zoneBox.SelectionChanged += (_, _) => OnZoneChanged();

        var reconnect = new Button { Content = "Обновить", Padding = new Thickness(12, 4, 12, 4) };
        reconnect.Click += (_, _) => Connect();

        root.Children.Add(Row("Устройство и зона:", _deviceBox, _zoneBox, reconnect));

        // длина зоны - вводится вручную, любая
        _lengthBox = new TextBox { Width = 70, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        _lengthBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyLength(); e.Handled = true; } };

        var applyLength = new Button { Content = "Применить", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        applyLength.Click += (_, _) => ApplyLength();

        root.Children.Add(Row("Длина зоны:", _lengthBox, applyLength,
                              LengthPreset("41", 41, "одна вертушка: 9 на крыльчатке + 32 на рамке"),
                              LengthPreset("120", 120, "потолок, который называет сервер для хедеров"),
                              LengthPreset("240", 240, "проверить, врёт ли потолок")));

        _limitText = new TextBlock { Margin = new Thickness(150, 0, 0, 6), Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_limitText);

        // крупный счётчик
        _indexText = new TextBlock
        {
            Text = "0",
            FontSize = 64,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _detailText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(_indexText);
        root.Children.Add(_detailText);

        // шаги
        var steps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        steps.Children.Add(StepButton("−10", -10));
        steps.Children.Add(StepButton("−1", -1));
        steps.Children.Add(StepButton("+1", +1));
        steps.Children.Add(StepButton("+10", +10));
        root.Children.Add(steps);

        // режим показа
        _modeSingle = new RadioButton { Content = "один диод", Margin = new Thickness(0, 0, 16, 0) };
        _modeGrow = new RadioButton { Content = "нарастающий (все до текущего)", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _modeMarks = new RadioButton { Content = "маркеры по 10" };

        foreach (var rb in new[] { _modeSingle, _modeGrow, _modeMarks })
            rb.Checked += (_, _) => Paint();

        root.Children.Add(Row("Показ:", _modeSingle, _modeGrow, _modeMarks));

        // цвет заливки
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        colors.Children.Add(ColorButton("белый", new RgbColor(255, 255, 255)));
        colors.Children.Add(ColorButton("красный", new RgbColor(255, 0, 0)));
        colors.Children.Add(ColorButton("зелёный", new RgbColor(0, 255, 0)));
        colors.Children.Add(ColorButton("синий", new RgbColor(0, 80, 255)));

        var blackout = new Button { Content = "Погасить всё", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(16, 0, 0, 0) };
        blackout.Click += (_, _) => Blackout();
        colors.Children.Add(blackout);

        root.Children.Add(Row("Цвет:", colors));

        _statusText = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        root.Children.Add(_statusText);

        var hint = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Text = "Клавиши: ← → на один диод, PgUp/PgDn на десять, Home в начало, End в конец.\n" +
                   "Ищем длину так: режим «нарастающий», держим →, пока новые диоды перестанут загораться."
        };
        root.Children.Add(hint);

        var window = new Window
        {
            Title = "Калибратор диодов",
            Content = new ScrollViewer { Content = root },
            Width = 760,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        window.KeyDown += OnKeyDown;
        return window;
    }

    static UIElement Row(string label, params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var i in items) panel.Children.Add(i);
        return panel;
    }

    /// <summary>Fills the length box and applies it in one press - these are the numbers asked for most.</summary>
    static Button LengthPreset(string caption, int value, string tooltip)
    {
        var b = new Button
        {
            Content = caption,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = tooltip
        };
        b.Click += (_, _) => { _lengthBox.Text = value.ToString(); ApplyLength(); };
        return b;
    }

    static Button StepButton(string caption, int delta)
    {
        var b = new Button
        {
            Content = caption,
            FontSize = 22,
            Width = 90,
            Height = 54,
            Margin = new Thickness(6, 0, 6, 0)
        };
        b.Click += (_, _) => Step(delta);
        return b;
    }

    static Button ColorButton(string caption, RgbColor colour)
    {
        var b = new Button
        {
            Content = caption,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(colour.R, colour.G, colour.B)),
            Foreground = Brushes.Black
        };
        b.Click += (_, _) => { _paint = colour; Paint(); };
        return b;
    }

    static void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right: Step(+1); break;
            case Key.Left: Step(-1); break;
            case Key.PageUp: Step(+10); break;
            case Key.PageDown: Step(-10); break;
            case Key.Home: SetIndex(0); break;
            case Key.End: SetIndex(ZoneCount() - 1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- связь с OpenRGB -------------------------------------------------

    static void Connect()
    {
        try
        {
            _client?.Dispose();
            _client = new OpenRgbClient(name: "RgbCalibrator");
            _devices = _client.GetAllControllerData();
        }
        catch (Exception ex)
        {
            _client = null;
            _devices = Array.Empty<Device>();
            Status("Нет связи с OpenRGB на 127.0.0.1:6742. Запусти его от администратора с ключом --server.\n" + ex.Message);
            return;
        }

        _deviceBox.Items.Clear();
        for (int i = 0; i < _devices.Length; i++)
        {
            // the controller list carries empty stubs; nothing to calibrate on those
            if (_devices[i].Leds.Length == 0) continue;

            string name = string.IsNullOrWhiteSpace(_devices[i].Name) ? "<без имени>" : _devices[i].Name;
            _deviceBox.Items.Add(new ComboItem($"[{i}] {name} — {_devices[i].Leds.Length} диодов", i));
        }

        if (_deviceBox.Items.Count > 0) _deviceBox.SelectedIndex = 0;
        Status($"Подключено. Контроллеров с диодами: {_deviceBox.Items.Count}.");
    }

    sealed record ComboItem(string Text, int Value)
    {
        public override string ToString() => Text;
    }

    static int CurrentDevice() => (_deviceBox.SelectedItem as ComboItem)?.Value ?? -1;
    static int CurrentZone() => (_zoneBox.SelectedItem as ComboItem)?.Value ?? -1;

    static int ZoneCount()
    {
        int d = CurrentDevice(), z = CurrentZone();
        if (d < 0 || z < 0) return 0;
        return (int)_devices[d].Zones[z].LedCount;
    }

    static void OnDeviceChanged()
    {
        int d = CurrentDevice();
        if (d < 0 || _client == null) return;

        // per-LED control, otherwise everything we send is quietly ignored
        try { _client.SetCustomMode(d); }
        catch (Exception ex) { Status("Не удалось включить по-диодный режим: " + ex.Message); }

        _zoneBox.Items.Clear();
        var zones = _devices[d].Zones;
        for (int z = 0; z < zones.Length; z++)
            _zoneBox.Items.Add(new ComboItem($"[{z}] {zones[z].Name} — {zones[z].LedCount}", z));

        if (_zoneBox.Items.Count > 0) _zoneBox.SelectedIndex = 0;
    }

    static void OnZoneChanged()
    {
        int d = CurrentDevice(), z = CurrentZone();
        if (d < 0 || z < 0) return;

        var zone = _devices[d].Zones[z];
        _lengthBox.Text = zone.LedCount.ToString();

        // Reported as information, not as a rule. The driver's idea of the limits is a
        // guess of its own, and refusing to try a larger number would leave devices it
        // pins at a single LED - the GPU among them - impossible to explore at all.
        _limitText.Text = zone.LedsMin == zone.LedsMax
            ? $"сервер считает длину жёсткой ({zone.LedsMin}), но попробовать можно любую"
            : $"сервер называет предел {zone.LedsMin}..{zone.LedsMax}";

        SetIndex(0);
    }

    static void ApplyLength()
    {
        int d = CurrentDevice(), z = CurrentZone();
        if (d < 0 || z < 0 || _client == null) return;

        if (!int.TryParse(_lengthBox.Text, out int size) || size < 0)
        {
            Status("Длина должна быть неотрицательным числом.");
            return;
        }

        var zone = _devices[d].Zones[z];
        bool beyond = size < zone.LedsMin || size > zone.LedsMax;

        try
        {
            _client.ResizeZone(d, z, size);

            // the layout changed underneath, so re-read rather than trust our copy
            _devices = _client.GetAllControllerData();
        }
        catch (Exception ex)
        {
            Status($"Сервер не принял длину {size}: {ex.Message}");
            return;
        }

        int keep = _zoneBox.SelectedIndex;
        OnDeviceChanged();
        if (keep >= 0 && keep < _zoneBox.Items.Count) _zoneBox.SelectedIndex = keep;

        // Asking is not getting: the server may quietly clamp, so report what actually
        // took effect rather than what we requested.
        int actual = ZoneCount();
        string note = actual == size
            ? $"Длина зоны задана: {actual}."
            : $"Просили {size}, сервер оставил {actual}.";

        if (beyond) note += $" (за пределом {zone.LedsMin}..{zone.LedsMax}, который он сам объявил)";

        Status(note);
    }

    // ---- показ -----------------------------------------------------------

    static void Step(int delta) => SetIndex(_index + delta);

    static void SetIndex(int value)
    {
        int count = ZoneCount();
        if (count <= 0) { _index = 0; _indexText.Text = "—"; _detailText.Text = ""; return; }

        _index = Math.Clamp(value, 0, count - 1);
        Paint();
    }

    static void Paint()
    {
        int d = CurrentDevice(), z = CurrentZone();
        if (d < 0 || z < 0 || _client == null) return;

        int count = ZoneCount();
        if (count <= 0) return;

        var colors = new RgbColor[count];
        var off = new RgbColor(0, 0, 0);

        for (int i = 0; i < count; i++)
        {
            if (_modeGrow.IsChecked == true)
                colors[i] = i <= _index ? _paint : off;
            else if (_modeMarks.IsChecked == true)
                colors[i] = i % 10 == 0 ? new RgbColor(255, 0, 0)
                          : i % 5 == 0 ? new RgbColor(0, 255, 0)
                          : new RgbColor(0, 0, 30);
            else
                colors[i] = i == _index ? _paint : off;
        }

        try { _client.UpdateZoneLeds(d, z, colors); }
        catch (Exception ex) { Status("Отправка не удалась: " + ex.Message); return; }

        // the global index is what the rest of the tooling addresses LEDs by
        int global = 0;
        for (int i = 0; i < z; i++) global += (int)_devices[d].Zones[i].LedCount;

        _indexText.Text = (_index + 1).ToString();
        _detailText.Text = $"диод {_index + 1} из {count} в зоне   •   индекс в зоне {_index}   •   в устройстве {global + _index}";
    }

    static void Blackout()
    {
        if (_client == null) return;

        for (int i = 0; i < _devices.Length; i++)
        {
            if (_devices[i].Leds.Length == 0) continue;
            try
            {
                _client.SetCustomMode(i);
                _client.UpdateLeds(i, Enumerable.Repeat(new RgbColor(0, 0, 0), _devices[i].Leds.Length).ToArray());
            }
            catch { /* устройство могло отвалиться, остальные всё равно гасим */ }
        }

        Status("Всё погашено.");
    }

    static void Status(string text) => _statusText.Text = text;
}
