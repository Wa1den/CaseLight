using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CaseLight.Model;
using CaseLight.Render;
using CaseLight.Rgb;
using CaseLight.View;

namespace CaseLight;

/// <summary>
/// Places every controllable light on one plane, the way it actually stands in the room,
/// and then paints it with what is on the screen.
///
/// The layout has to be honest first: a fan standing edge-on beside the monitor cannot be
/// described by "LED 40 of 68", only by where that LED physically is. Everything else -
/// which patch of screen it echoes, how often it is written - follows from that.
/// </summary>
public sealed class MainWindow : Window
{
    readonly RgbHub _hub = new();
    readonly SceneView _view = new();
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    Scene _scene = Scene.Load();
    CasePainter _painter = null!;

    ListBox _fixtureList = null!;
    StackPanel _settings = null!;
    StackPanel _properties = null!;
    TextBlock _status = null!;
    Button _runButton = null!;

    bool _syncing;

    public MainWindow()
    {
        Title = "CaseLight — подсветка корпуса";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(30, 32, 38));

        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/icon.ico")); }
        catch { /* без иконки окно всё равно работает */ }

        Content = BuildLayout();

        _painter = new CasePainter(_hub, _scene);

        _view.Scene = _scene;
        _view.SelectionChanged += (_, _) => { SyncFixtureList(); BuildProperties(); };
        _view.FixtureChanged += (_, _) => { _painter.Invalidate(); BuildProperties(); };

        // While painting, the status line is the only sign anything is happening at all.
        _statusTimer.Tick += (_, _) => { if (_painter.IsRunning) Say(_painter.Status); };
        _statusTimer.Start();

        Loaded += (_, _) =>
        {
            ConnectHub();
            SyncFixtureList();
            _view.FitToContent();

            // On a cold boot OpenRGB may not be up yet; the paint loop waits for it by
            // itself, so starting here is safe even when nothing is listening.
            if (_scene.StartPaintingOnLaunch) TogglePainting();
        };

        Closed += (_, _) => { _painter.Dispose(); _scene.Save(); _hub.Dispose(); };
    }

    // ---- каркас -----------------------------------------------------------

    UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(BuildLeftColumn());

        // ---- центр: сцена
        var host = new Border { Margin = new Thickness(0, 10, 0, 10), Child = _view };
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        // ---- справа: только выбранная фигура
        _properties = new StackPanel { Margin = new Thickness(10) };
        var scroll = new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);

        // ---- низ: действия и статус
        var bottom = new StackPanel { Margin = new Thickness(10), Orientation = Orientation.Horizontal };

        _runButton = SmallButton("Пуск раскраски", TogglePainting);
        bottom.Children.Add(_runButton);
        bottom.Children.Add(SmallButton("Погасить", () => { _hub.Blackout(); Say("Подсветка погашена."); }));
        bottom.Children.Add(SmallButton("Переподключиться", ConnectHub));
        bottom.Children.Add(SmallButton("Вписать в окно", () => _view.FitToContent()));
        bottom.Children.Add(SmallButton("Сохранить", () => { _scene.Save(); Say("Раскладка сохранена: " + Scene.DefaultPath); }));

        _status = new TextBlock
        {
            Foreground = Brushes.Silver,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        bottom.Children.Add(_status);

        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        grid.Children.Add(bottom);

        return grid;
    }

    /// <summary>
    /// Fixtures on top, everything that is not about a fixture underneath. There will never
    /// be many devices, so the list does not need the whole height, and the general
    /// settings are better here than mixed into the per-fixture panel.
    /// </summary>
    UIElement BuildLeftColumn()
    {
        var column = new Grid { Margin = new Thickness(10) };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star) });

        var caption = Header("Фигуры");
        Grid.SetRow(caption, 0);
        column.Children.Add(caption);

        _fixtureList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 41, 48)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _fixtureList.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _view.Select((_fixtureList.SelectedItem as FixtureItem)?.Fixture);
        };
        Grid.SetRow(_fixtureList, 1);
        column.Children.Add(_fixtureList);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("Добавить", AddFixture));
        buttons.Children.Add(SmallButton("Копия", DuplicateFixture));
        buttons.Children.Add(SmallButton("Удалить", RemoveFixture));
        Grid.SetRow(buttons, 2);
        column.Children.Add(buttons);

        _settings = new StackPanel();
        var settingsScroll = new ScrollViewer
        {
            Content = _settings,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(settingsScroll, 3);
        column.Children.Add(settingsScroll);

        Grid.SetColumn(column, 0);
        return column;
    }

    sealed record FixtureItem(Fixture Fixture)
    {
        public override string ToString() =>
            (Fixture.Enabled ? "" : "· выкл · ") + $"{Fixture.Name}  ({Fixture.LedCount})";
    }

    void SyncFixtureList()
    {
        _syncing = true;
        _fixtureList.Items.Clear();
        foreach (var f in _scene.Fixtures) _fixtureList.Items.Add(new FixtureItem(f));

        _fixtureList.SelectedItem = _fixtureList.Items.Cast<FixtureItem>()
                                                     .FirstOrDefault(i => i.Fixture == _view.Selected);
        _syncing = false;
    }

    // ---- подключение и раскраска ------------------------------------------

    void ConnectHub()
    {
        _hub.Connect(force: true);
        Say(_hub.Status);
        BuildProperties();
    }

    void Say(string text) => _status.Text = text;

    /// <summary>Anything that changed the layout has to reach the canvas and the painter alike.</summary>
    void Touch()
    {
        _view.InvalidateVisual();
        _painter?.Invalidate();
    }

    void TogglePainting()
    {
        if (_painter.IsRunning)
        {
            _painter.Stop();
            _runButton.Content = "Пуск раскраски";
            Say("Раскраска остановлена, подсветка погашена.");
            return;
        }

        if (!_hub.Connect(force: true)) { Say(_hub.Status); return; }

        _painter.UseScene(_scene);
        _painter.Start();
        _runButton.Content = "Остановить";
        Say("Раскраска запущена. Если кадров нет — включи в Ambilight «Отдавать снимки экрана в модуль подсветки».");
    }

    // ---- фигуры -----------------------------------------------------------

    void AddFixture()
    {
        var f = new Fixture
        {
            Name = "Фигура " + (_scene.Fixtures.Count + 1),
            CenterX = _scene.Monitor.CenterX + _scene.Monitor.Width / 2 + 200,
            CenterY = _scene.Monitor.CenterY,
            Width = 120,
            Height = 120
        };

        // Bind to something real straight away when possible: an unbound fixture has no
        // LED count, so it would draw as an empty rectangle and look broken.
        var first = _hub.Devices.FirstOrDefault();
        if (first != null && first.Zones.Length > 0)
        {
            f.Binding.DeviceName = first.Name;
            f.Binding.DeviceLocation = first.Location;
            f.Binding.ZoneIndex = 0;
            f.Binding.FirstLed = 0;
            f.Binding.LedCount = first.Zones[0].LedCount;
        }

        lock (_scene.Fixtures) _scene.Fixtures.Add(f);
        _view.Select(f);
        SyncFixtureList();
        Touch();
    }

    void DuplicateFixture()
    {
        if (_view.Selected == null) return;

        var copy = _view.Selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = _view.Selected.Name + " (копия)";
        copy.CenterX += 60;
        copy.CenterY += 60;

        lock (_scene.Fixtures) _scene.Fixtures.Add(copy);
        _view.Select(copy);
        SyncFixtureList();
        Touch();
    }

    void RemoveFixture()
    {
        if (_view.Selected == null) return;
        lock (_scene.Fixtures) _scene.Fixtures.Remove(_view.Selected);
        _view.Select(null);
        SyncFixtureList();
        Touch();
    }

    void HighlightSelected()
    {
        if (_view.Selected == null) { Say("Сначала выбери фигуру."); return; }
        if (!_hub.Connect()) { Say(_hub.Status); return; }

        if (_painter.IsRunning)
        {
            Say("Сначала останови раскраску — иначе она сразу перекрасит подсветку обратно.");
            return;
        }

        _hub.Highlight(_view.Selected, 255, 255, 255);
        Say($"Горит только «{_view.Selected.Name}» — так её видно в корпусе.");
    }

    // ---- панели -----------------------------------------------------------

    void BuildProperties()
    {
        BuildSettings();
        BuildFixturePanel();
    }

    /// <summary>Everything that is not about one particular fixture.</summary>
    void BuildSettings()
    {
        _settings.Children.Clear();

        _settings.Children.Add(Header("Монитор"));
        _settings.Children.Add(Note("Размер видимой картинки. От него считается, какой участок экрана видит каждый диод."));
        _settings.Children.Add(Num("Ширина, мм", _scene.Monitor.Width, v => { _scene.Monitor.Width = Math.Max(10, v); Touch(); }));
        _settings.Children.Add(Num("Высота, мм", _scene.Monitor.Height, v => { _scene.Monitor.Height = Math.Max(10, v); Touch(); }));

        _settings.Children.Add(Header("Раскраска"));
        _settings.Children.Add(Num("Область выборки, мм", _scene.SampleRadiusMm, v => { _scene.SampleRadiusMm = Math.Max(1, v); Touch(); }));
        _settings.Children.Add(Note("Сколько экрана усредняет один диод. Мало — дёргается на любом движении, много — всё сливается в один бурый цвет."));

        _settings.Children.Add(Int("Кадров в секунду", _scene.MaxFps, v => _scene.MaxFps = Math.Clamp(v, 1, 120)));
        _settings.Children.Add(Num("Яркость, 0..1", _scene.Brightness, v => _scene.Brightness = Math.Clamp(v, 0, 1)));
        _settings.Children.Add(Num("Насыщенность", _scene.Saturation, v => _scene.Saturation = Math.Clamp(v, 0, 3)));
        _settings.Children.Add(Num("Гамма", _scene.Gamma, v => _scene.Gamma = Math.Clamp(v, 0.1, 5)));
        _settings.Children.Add(Num("Порог темноты", _scene.MinLuma, v => _scene.MinLuma = Math.Clamp(v, 0, 1)));
        _settings.Children.Add(Note("Ниже этой яркости диод гаснет совсем, чтобы почти чёрный экран не оставлял корпус тускло подсвеченным."));

        _settings.Children.Add(Num("Разгорается за", _scene.SmoothingRise, v => _scene.SmoothingRise = Math.Clamp(v, 0.01, 1)));
        _settings.Children.Add(Num("Гаснет за", _scene.SmoothingFall, v => _scene.SmoothingFall = Math.Clamp(v, 0.01, 1)));
        _settings.Children.Add(Note("Больше — быстрее. Свет привычнее выглядит, когда нарастает резко, а спадает плавно."));

        _settings.Children.Add(Header("Программа"));
        _settings.Children.Add(Check("Запускать вместе с Windows", Autostart.IsEnabled(), v => Say(Autostart.Set(v))));
        _settings.Children.Add(Check("Сразу начинать раскраску", _scene.StartPaintingOnLaunch, v => _scene.StartPaintingOnLaunch = v));
        _settings.Children.Add(Note("Подсветкой управляет OpenRGB, и ему нужны права администратора. Автозапуск CaseLight поможет только если и OpenRGB стартует сам."));
    }

    void BuildFixturePanel()
    {
        _properties.Children.Clear();

        var f = _view.Selected;
        if (f == null)
        {
            _properties.Children.Add(Header("Фигура"));
            _properties.Children.Add(Note("Выбери фигуру на сцене или в списке слева."));
            return;
        }

        _properties.Children.Add(Header("Фигура"));
        _properties.Children.Add(Text("Название", f.Name, v => { f.Name = v; SyncFixtureList(); Touch(); }));
        _properties.Children.Add(Check("Участвует в раскраске", f.Enabled, v => { f.Enabled = v; SyncFixtureList(); Touch(); }));

        _properties.Children.Add(Int("Обновлять раз в N кадров", f.UpdateEvery, v => { f.UpdateEvery = Math.Max(1, v); Touch(); }));
        _properties.Children.Add(Note("1 — каждый кадр. Оперативной памяти нужно больше: она сидит на медленной шине SMBus, " +
                                      "и запись в неё каждый кадр задерживает всё остальное. 10–15 для памяти обычно достаточно."));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        actions.Children.Add(SmallButton("Найти в корпусе", HighlightSelected));
        _properties.Children.Add(actions);

        // ---- привязка
        _properties.Children.Add(Header("Привязка к железу"));

        var deviceBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var d in _hub.Devices) deviceBox.Items.Add(d.Name);
        deviceBox.SelectedItem = _hub.Devices.FirstOrDefault(d => d.Name == f.Binding.DeviceName)?.Name;
        deviceBox.SelectionChanged += (_, _) =>
        {
            if (deviceBox.SelectedItem is not string name) return;
            var dev = _hub.Devices.First(d => d.Name == name);
            f.Binding.DeviceName = dev.Name;
            f.Binding.DeviceLocation = dev.Location;
            f.Binding.ZoneIndex = 0;
            f.Binding.FirstLed = 0;
            f.Binding.LedCount = dev.Zones.FirstOrDefault()?.LedCount ?? 0;
            BuildProperties();
            Touch();
        };
        _properties.Children.Add(Labelled("Контроллер", deviceBox));

        var info = _hub.Find(f.Binding);
        if (info != null)
        {
            var zoneBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            foreach (var z in info.Zones) zoneBox.Items.Add($"[{z.Index}] {z.Name} — {z.LedCount}");
            if (f.Binding.ZoneIndex < zoneBox.Items.Count) zoneBox.SelectedIndex = f.Binding.ZoneIndex;

            zoneBox.SelectionChanged += (_, _) =>
            {
                if (zoneBox.SelectedIndex < 0) return;
                f.Binding.ZoneIndex = zoneBox.SelectedIndex;
                f.Binding.FirstLed = 0;
                f.Binding.LedCount = info.Zones[zoneBox.SelectedIndex].LedCount;
                BuildProperties();
                Touch();
            };
            _properties.Children.Add(Labelled("Зона (разъём)", zoneBox));
        }
        else
        {
            _properties.Children.Add(Note($"Контроллер «{f.Binding.DeviceName}» сейчас не виден. " +
                                          "Проверь, что OpenRGB запущен от администратора, и нажми «Переподключиться»."));
        }

        _properties.Children.Add(Int("Первый диод зоны", f.Binding.FirstLed, v => { f.Binding.FirstLed = Math.Max(0, v); Touch(); }));
        _properties.Children.Add(Int("Сколько диодов", f.Binding.LedCount, v => { f.Binding.LedCount = Math.Max(0, v); Touch(); }));
        _properties.Children.Add(Note("Если на одном разъёме несколько разных вещей, их можно развести по фигурам, поделив диапазон."));

        // ---- место
        _properties.Children.Add(Header("Место в корпусе, мм"));
        _properties.Children.Add(Num("Центр по горизонтали", f.CenterX, v => { f.CenterX = v; Touch(); }));
        _properties.Children.Add(Num("Центр по вертикали", f.CenterY, v => { f.CenterY = v; Touch(); }));
        _properties.Children.Add(Num("Ширина", f.Width, v => { f.Width = Math.Max(5, v); Touch(); }));
        _properties.Children.Add(Num("Высота", f.Height, v => { f.Height = Math.Max(5, v); Touch(); }));
        _properties.Children.Add(Num("Поворот, градусов", f.AngleDeg, v => { f.AngleDeg = v; Touch(); }));

        // ---- раскладка
        _properties.Children.Add(Header("Как идут диоды"));

        var kindBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        kindBox.Items.Add("Полоса — у ленты есть два конца");
        kindBox.Items.Add("Замкнутое — кольцо или рамка");
        kindBox.Items.Add("Точка — всё светится в одном месте");
        kindBox.SelectedIndex = f.Arrangement switch
        {
            Arrangement.Strip => 0,
            Arrangement.Closed => 1,
            _ => 2
        };
        kindBox.SelectionChanged += (_, _) =>
        {
            f.Arrangement = kindBox.SelectedIndex switch
            {
                0 => Arrangement.Strip,
                1 => Arrangement.Closed,
                _ => Arrangement.Point
            };
            BuildProperties();
            Touch();
        };
        _properties.Children.Add(Labelled("Форма", kindBox));

        if (f.Arrangement == Arrangement.Closed)
        {
            _properties.Children.Add(Check("Контур круглый", f.RoundContour, v => { f.RoundContour = v; BuildProperties(); Touch(); }));

            if (!f.RoundContour)
            {
                _properties.Children.Add(Num("Пропорция рамки (высота ÷ ширина)", f.ContourAspect,
                                             v => { f.ContourAspect = Math.Max(0.05, v); Touch(); }));
                _properties.Children.Add(Note("Форма самой рамки, а не её места на сцене. У рамки тройной вертушки это примерно 3."));
            }

            _properties.Children.Add(Note("У замкнутого контура нет своего первого диода — его надо назначить. " +
                                          "Для вертушки, стоящей ребром, это тот, что физически внизу."));
        }

        if (f.Arrangement != Arrangement.Point)
        {
            _properties.Children.Add(AnchorRow(f));
            _properties.Children.Add(Check("Обход в другую сторону", f.Reverse, v => { f.Reverse = v; Touch(); }));
        }

        if (f.Arrangement == Arrangement.Closed)
        {
            _properties.Children.Add(Check("Стоит ребром ко мне", f.EdgeOn, v => { f.EdgeOn = v; BuildProperties(); Touch(); }));

            if (f.EdgeOn)
                _properties.Children.Add(Note("Кольцо видно с торца, поэтому оно сжимается в вертикальную линию: " +
                                              "от начального диода высота растёт в обе стороны и сходится наверху. " +
                                              "Ширина фигуры на цвет тогда не влияет."));
        }
    }

    /// <summary>
    /// The anchor with its own stepper and a button that lights just that LED - the only
    /// reliable way to find which one is physically at the bottom is to look at it.
    /// </summary>
    UIElement AnchorRow(Fixture f)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = "Начальный диод", Foreground = Brushes.Silver, FontSize = 11 });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        var value = new TextBlock
        {
            Text = f.AnchorLed.ToString(),
            Foreground = Brushes.White,
            FontSize = 18,
            Width = 46,
            VerticalAlignment = VerticalAlignment.Center
        };

        void Move(int delta)
        {
            int n = Math.Max(1, f.LedCount);
            f.AnchorLed = ((f.AnchorLed + delta) % n + n) % n;
            value.Text = f.AnchorLed.ToString();
            Touch();
            ShowAnchor(f);
        }

        row.Children.Add(SmallButton("−", () => Move(-1)));
        row.Children.Add(value);
        row.Children.Add(SmallButton("+", () => Move(+1)));
        row.Children.Add(SmallButton("показать", () => ShowAnchor(f)));

        panel.Children.Add(row);
        return panel;
    }

    void ShowAnchor(Fixture f)
    {
        if (_painter.IsRunning)
        {
            Say("Останови раскраску, иначе она сразу перекрасит подсветку обратно.");
            return;
        }

        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _hub.HighlightLed(f, f.AnchorLed, 255, 60, 0);
        Say($"Горит только диод {f.AnchorLed} — он считается началом.");
    }

    // ---- мелочи интерфейса ------------------------------------------------

    static TextBlock Header(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 12, 0, 6)
    };

    static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(150, 158, 172)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8),
        FontSize = 11
    };

    static UIElement Labelled(string label, UIElement editor)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Silver, FontSize = 11 });
        panel.Children.Add(editor);
        return panel;
    }

    static UIElement Text(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => set(box.Text);
        return Labelled(label, box);
    }

    /// <summary>Accepts both a comma and a dot, because both get typed.</summary>
    static UIElement Num(string label, double value, Action<double> set)
    {
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture), Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                set(v);
        };
        return Labelled(label, box);
    }

    static UIElement Int(string label, int value, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out int v)) set(v); };
        return Labelled(label, box);
    }

    static UIElement Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = Brushes.Silver,
            Margin = new Thickness(0, 6, 0, 2)
        };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    static Button SmallButton(string caption, Action onClick)
    {
        var b = new Button { Content = caption, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
