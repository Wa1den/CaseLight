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
/// Places every controllable light on one plane, the way it actually stands in the room.
///
/// The screen-following part comes later; what has to exist first is an honest map. A fan
/// standing edge-on beside the monitor cannot be described by "LED number 40 of 68" - only
/// by where that LED physically is, which is what this window is for.
/// </summary>
public sealed class MainWindow : Window
{
    readonly RgbHub _hub = new();
    readonly SceneView _view = new();
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    Scene _scene = Scene.Load();
    CasePainter _painter = null!;

    ListBox _fixtureList = null!;
    StackPanel _properties = null!;
    TextBlock _status = null!;

    bool _syncing;

    public MainWindow()
    {
        Title = "CaseLight — раскладка подсветки";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(30, 32, 38));

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
        };

        Closed += (_, _) => { _painter.Dispose(); _scene.Save(); _hub.Dispose(); };
    }

    // ---- каркас -----------------------------------------------------------

    UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- слева: фигуры
        var left = new DockPanel { Margin = new Thickness(10) };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(SmallButton("Добавить", AddFixture));
        addRow.Children.Add(SmallButton("Копия", DuplicateFixture));
        addRow.Children.Add(SmallButton("Удалить", RemoveFixture));
        DockPanel.SetDock(addRow, Dock.Bottom);
        left.Children.Add(addRow);

        var caption = Header("Фигуры");
        DockPanel.SetDock(caption, Dock.Top);
        left.Children.Add(caption);

        _fixtureList = new ListBox { Background = new SolidColorBrush(Color.FromRgb(38, 41, 48)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        _fixtureList.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _view.Select((_fixtureList.SelectedItem as FixtureItem)?.Fixture);
        };
        left.Children.Add(_fixtureList);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ---- центр: сцена
        var host = new Border { Margin = new Thickness(0, 10, 0, 10), Child = _view };
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        // ---- справа: свойства
        _properties = new StackPanel { Margin = new Thickness(10) };
        var scroll = new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);

        // ---- низ: действия и статус
        var bottom = new StackPanel { Margin = new Thickness(10), Orientation = Orientation.Horizontal };
        bottom.Children.Add(SmallButton("Подключиться", ConnectHub));
        bottom.Children.Add(SmallButton("Пуск раскраски", StartPainting));
        bottom.Children.Add(SmallButton("Стоп", StopPainting));
        bottom.Children.Add(SmallButton("Подсветить фигуру", HighlightSelected));
        bottom.Children.Add(SmallButton("Погасить", () => { _hub.Blackout(); Say("Погашено."); }));
        bottom.Children.Add(SmallButton("Вписать вид", () => _view.FitToContent()));
        bottom.Children.Add(SmallButton("Сохранить", () => { _scene.Save(); Say("Сохранено: " + Scene.DefaultPath); }));

        _status = new TextBlock { Foreground = Brushes.Silver, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        bottom.Children.Add(_status);

        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        grid.Children.Add(bottom);

        return grid;
    }

    sealed record FixtureItem(Fixture Fixture)
    {
        public override string ToString() =>
            $"{Fixture.Name}  ·  {Fixture.LedCount} диодов";
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

    // ---- подключение ------------------------------------------------------

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

    void StartPainting()
    {
        if (!_hub.Connect(force: true)) { Say(_hub.Status); return; }

        _painter.UseScene(_scene);
        _painter.Start();
        Say("Раскраска запущена. Если кадров нет - включи в Ambilight «отдавать снимки экрана в модуль подсветки».");
    }

    void StopPainting()
    {
        _painter.Stop();
        Say("Раскраска остановлена, подсветка погашена.");
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

        _scene.Fixtures.Add(f);
        _view.Select(f);
        SyncFixtureList();
    }

    void DuplicateFixture()
    {
        if (_view.Selected == null) return;

        var copy = _view.Selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = _view.Selected.Name + " (копия)";
        copy.CenterX += 60;
        copy.CenterY += 60;

        _scene.Fixtures.Add(copy);
        _view.Select(copy);
        SyncFixtureList();
    }

    void RemoveFixture()
    {
        if (_view.Selected == null) return;
        _scene.Fixtures.Remove(_view.Selected);
        _view.Select(null);
        SyncFixtureList();
    }

    void HighlightSelected()
    {
        if (_view.Selected == null) { Say("Сначала выбери фигуру."); return; }
        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _hub.Highlight(_view.Selected, 255, 255, 255);
        Say($"Подсвечена «{_view.Selected.Name}», остальное погашено.");
    }

    // ---- панель свойств ---------------------------------------------------

    void BuildProperties()
    {
        _properties.Children.Clear();

        _properties.Children.Add(Header("Монитор"));
        _properties.Children.Add(Num("Ширина, мм", _scene.Monitor.Width, v => { _scene.Monitor.Width = v; Touch(); }));
        _properties.Children.Add(Num("Высота, мм", _scene.Monitor.Height, v => { _scene.Monitor.Height = v; Touch(); }));

        _properties.Children.Add(Header("Раскраска"));
        _properties.Children.Add(Num("Область выборки, мм", _scene.SampleRadiusMm, v => { _scene.SampleRadiusMm = Math.Max(1, v); Touch(); }));
        _properties.Children.Add(Note("Сколько экрана усредняет один диод. Мало — дёргается на любом движении, много — всё сливается в один бурый цвет."));
        _properties.Children.Add(Int("Кадров в секунду", _scene.MaxFps, v => _scene.MaxFps = Math.Clamp(v, 1, 120)));
        _properties.Children.Add(Num("Яркость (0..1)", _scene.Brightness, v => _scene.Brightness = Math.Clamp(v, 0, 1)));
        _properties.Children.Add(Num("Гамма", _scene.Gamma, v => _scene.Gamma = Math.Clamp(v, 0.1, 5)));
        _properties.Children.Add(Num("Насыщенность", _scene.Saturation, v => _scene.Saturation = Math.Clamp(v, 0, 3)));
        _properties.Children.Add(Num("Порог темноты", _scene.MinLuma, v => _scene.MinLuma = Math.Clamp(v, 0, 1)));
        _properties.Children.Add(Num("Сглаживание вверх", _scene.SmoothingRise, v => _scene.SmoothingRise = Math.Clamp(v, 0.01, 1)));
        _properties.Children.Add(Num("Сглаживание вниз", _scene.SmoothingFall, v => _scene.SmoothingFall = Math.Clamp(v, 0.01, 1)));

        var f = _view.Selected;
        if (f == null)
        {
            _properties.Children.Add(Note("Выбери фигуру на сцене или в списке слева."));
            return;
        }

        _properties.Children.Add(Header("Фигура"));
        _properties.Children.Add(Text("Имя", f.Name, v => { f.Name = v; SyncFixtureList(); Touch(); }));

        // ---- привязка
        _properties.Children.Add(Header("Что светится"));

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
        _properties.Children.Add(Labelled("Устройство", deviceBox));

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
            _properties.Children.Add(Labelled("Зона", zoneBox));
        }
        else
        {
            _properties.Children.Add(Note("Устройство сейчас не найдено — проверь, что OpenRGB запущен."));
        }

        _properties.Children.Add(Int("Первый диод", f.Binding.FirstLed, v => { f.Binding.FirstLed = Math.Max(0, v); Touch(); }));
        _properties.Children.Add(Int("Сколько диодов", f.Binding.LedCount, v => { f.Binding.LedCount = Math.Max(0, v); Touch(); }));

        // ---- место
        _properties.Children.Add(Header("Место на сцене, мм"));
        _properties.Children.Add(Num("Центр X", f.CenterX, v => { f.CenterX = v; Touch(); }));
        _properties.Children.Add(Num("Центр Y", f.CenterY, v => { f.CenterY = v; Touch(); }));
        _properties.Children.Add(Num("Ширина", f.Width, v => { f.Width = Math.Max(5, v); Touch(); }));
        _properties.Children.Add(Num("Высота", f.Height, v => { f.Height = Math.Max(5, v); Touch(); }));
        _properties.Children.Add(Num("Поворот, °", f.AngleDeg, v => { f.AngleDeg = v; Touch(); }));

        // ---- раскладка
        _properties.Children.Add(Header("Как идут диоды"));

        var kindBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        kindBox.Items.Add("Полоса (два конца)");
        kindBox.Items.Add("Замкнутое (кольцо, рамка)");
        kindBox.Items.Add("Точка (всё в одном месте)");
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
                _properties.Children.Add(Num("Пропорция контура (В/Ш)", f.ContourAspect, v => { f.ContourAspect = Math.Max(0.05, v); Touch(); }));

            _properties.Children.Add(Note("У замкнутого контура нет своего первого диода — его надо назначить. " +
                                          "Для вертушки, стоящей ребром, это тот, что физически внизу."));
        }

        _properties.Children.Add(AnchorRow(f));
        _properties.Children.Add(Check("Обход в другую сторону", f.Reverse, v => { f.Reverse = v; Touch(); }));
        _properties.Children.Add(Check("Видно с ребра (схлопнуть по вертикали)", f.EdgeOn, v => { f.EdgeOn = v; Touch(); }));

        if (f.EdgeOn)
            _properties.Children.Add(Note("Диоды сведены на одну вертикаль: от начального вверх в обе стороны, " +
                                          "как и видно у вертушки, повёрнутой ребром."));
    }

    /// <summary>
    /// The anchor with its own stepper and a button that lights just that LED - the only
    /// reliable way to find which one is physically at the bottom is to look at it.
    /// </summary>
    UIElement AnchorRow(Fixture f)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = "Начальный диод", Foreground = Brushes.Silver });

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

            if (_hub.Connect()) _hub.HighlightLed(f, f.AnchorLed, 255, 60, 0);
        }

        row.Children.Add(SmallButton("−", () => Move(-1)));
        row.Children.Add(value);
        row.Children.Add(SmallButton("+", () => Move(+1)));
        row.Children.Add(SmallButton("показать", () =>
        {
            if (_hub.Connect()) _hub.HighlightLed(f, f.AnchorLed, 255, 60, 0);
            Say($"Горит только диод {f.AnchorLed} — он и считается началом.");
        }));

        panel.Children.Add(row);
        return panel;
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
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.Silver, Margin = new Thickness(0, 6, 0, 2) };
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
