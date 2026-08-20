using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CaseLight.Model;
using CaseLight.Render;

namespace CaseLight;

/// <summary>
/// The fixture panel, the colours tab and the placement test.
///
/// Split off from the window frame simply because it is the long half - the frame is tabs
/// and buttons, this is everything the tabs actually contain.
/// </summary>
public sealed partial class MainWindow
{
    // ---- список фигур -----------------------------------------------------

    sealed record FixtureItem(Fixture Fixture)
    {
        public override string ToString() =>
            (Fixture.Enabled ? "" : "· выкл · ") + $"{Fixture.Name}  ({Fixture.LedCount})";
    }

    void SyncFixtureList()
    {
        if (_fixtureList == null) return;

        _syncingList = true;
        _fixtureList.Items.Clear();
        foreach (var f in _scene.Fixtures) _fixtureList.Items.Add(new FixtureItem(f));

        _fixtureList.SelectedItem = _fixtureList.Items.Cast<FixtureItem>()
                                                     .FirstOrDefault(i => i.Fixture == _view.Selected);
        _syncingList = false;
    }

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

    // ---- панель фигуры поверх холста --------------------------------------

    void ShowFixturePanel()
    {
        if (_view.Selected == null) { HideFixturePanel(); return; }

        BuildFixturePanel();
        _fixtureOverlay.Visibility = Visibility.Visible;
    }

    void HideFixturePanel()
    {
        if (_fixtureOverlay != null) _fixtureOverlay.Visibility = Visibility.Collapsed;
    }

    void BuildFixturePanel()
    {
        if (_fixturePanel == null) return;

        _fixturePanel.Children.Clear();

        var f = _view.Selected;
        if (f == null) return;

        // заголовок с крестиком
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var close = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            ToolTip = "Закрыть параметры"
        };
        close.Click += (_, _) => HideFixturePanel();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock
        {
            Text = "Параметры фигуры",
            Foreground = Ui.Fg,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        _fixturePanel.Children.Add(head);

        _fixturePanel.Children.Add(Ui.Text("Название", f.Name, v => { f.Name = v; SyncFixtureList(); Touch(); }));
        _fixturePanel.Children.Add(Ui.Check("Участвует в раскраске", f.Enabled, v => { f.Enabled = v; SyncFixtureList(); Touch(); }));
        _fixturePanel.Children.Add(Ui.Int("Обновлять раз в N кадров", f.UpdateEvery, v => { f.UpdateEvery = Math.Max(1, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.Note("1 — каждый кадр. Оперативной памяти нужно больше: она сидит на медленной шине SMBus, " +
                                           "и запись в неё каждый кадр задерживает всё остальное. 10–15 обычно достаточно."));

        _fixturePanel.Children.Add(Ui.Row(Ui.Btn("Найти в корпусе", HighlightSelected)));

        // ---- привязка
        _fixturePanel.Children.Add(Ui.Header("Привязка к железу"));

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

            BuildFixturePanel();
            Touch();
        };
        _fixturePanel.Children.Add(Ui.Labelled("Контроллер", deviceBox));

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

                BuildFixturePanel();
                Touch();
            };
            _fixturePanel.Children.Add(Ui.Labelled("Зона (разъём)", zoneBox));
        }
        else
        {
            _fixturePanel.Children.Add(Ui.Note($"Контроллер «{f.Binding.DeviceName}» сейчас не виден. " +
                                               "Проверь, что OpenRGB запущен от администратора, и нажми «Переподключиться»."));
        }

        _fixturePanel.Children.Add(Ui.Int("Первый диод зоны", f.Binding.FirstLed, v => { f.Binding.FirstLed = Math.Max(0, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.Int("Сколько диодов", f.Binding.LedCount, v => { f.Binding.LedCount = Math.Max(0, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.Note("Если на одном разъёме несколько разных вещей, их можно развести по фигурам, поделив диапазон."));

        // ---- место
        _fixturePanel.Children.Add(Ui.Header("Место в корпусе, мм"));
        _fixturePanel.Children.Add(Ui.Num("Центр по горизонтали", f.CenterX, v => { f.CenterX = v; Touch(); }));
        _fixturePanel.Children.Add(Ui.Num("Центр по вертикали", f.CenterY, v => { f.CenterY = v; Touch(); }));
        _fixturePanel.Children.Add(Ui.Num("Ширина", f.Width, v => { f.Width = Math.Max(5, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.Num("Высота", f.Height, v => { f.Height = Math.Max(5, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.Num("Поворот, градусов", f.AngleDeg, v => { f.AngleDeg = v; Touch(); }));

        // ---- раскладка
        _fixturePanel.Children.Add(Ui.Header("Как идут диоды"));

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
            BuildFixturePanel();
            Touch();
        };
        _fixturePanel.Children.Add(Ui.Labelled("Форма", kindBox));

        if (f.Arrangement == Arrangement.Closed)
        {
            _fixturePanel.Children.Add(Ui.Check("Контур круглый", f.RoundContour, v => { f.RoundContour = v; BuildFixturePanel(); Touch(); }));

            if (!f.RoundContour)
            {
                _fixturePanel.Children.Add(Ui.Num("Пропорция рамки (высота ÷ ширина)", f.ContourAspect,
                                                  v => { f.ContourAspect = Math.Max(0.05, v); Touch(); }));
                _fixturePanel.Children.Add(Ui.Note("Форма самой рамки, а не её места на сцене. У рамки тройной вертушки это примерно 3."));
            }

            _fixturePanel.Children.Add(Ui.Note("У замкнутого контура нет своего первого диода — его надо назначить. " +
                                               "Для вертушки, стоящей ребром, это тот, что физически внизу."));
        }

        if (f.Arrangement != Arrangement.Point)
        {
            _fixturePanel.Children.Add(AnchorRow(f));
            _fixturePanel.Children.Add(Ui.Check("Обход в другую сторону", f.Reverse, v => { f.Reverse = v; Touch(); }));
        }

        if (f.Arrangement == Arrangement.Closed)
        {
            _fixturePanel.Children.Add(Ui.Check("Стоит ребром ко мне", f.EdgeOn, v => { f.EdgeOn = v; BuildFixturePanel(); Touch(); }));

            if (f.EdgeOn)
                _fixturePanel.Children.Add(Ui.Note("Кольцо видно с торца, поэтому оно сжимается в вертикальную линию: " +
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
        panel.Children.Add(new TextBlock { Text = "Начальный диод", Foreground = Ui.FgDim, FontSize = 11 });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        var value = new TextBlock
        {
            Text = f.AnchorLed.ToString(),
            Foreground = Ui.Fg,
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

        row.Children.Add(Ui.Btn("−", () => Move(-1)));
        row.Children.Add(value);
        row.Children.Add(Ui.Btn("+", () => Move(+1)));
        row.Children.Add(Ui.Btn("показать", () => ShowAnchor(f)));

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

    void HighlightSelected()
    {
        if (_view.Selected == null) { Say("Сначала выбери фигуру."); return; }

        if (_painter.IsRunning)
        {
            Say("Сначала останови раскраску — иначе она сразу перекрасит подсветку обратно.");
            return;
        }

        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _hub.Highlight(_view.Selected, 255, 255, 255);
        Say($"Горит только «{_view.Selected.Name}» — так её видно в корпусе.");
    }

    // ---- вкладка цветов и тест размещения ---------------------------------

    void BuildColorsTab() => AddTab("Цвета", panel =>
    {
        panel.Children.Add(Ui.Header("Как берётся цвет"));
        panel.Children.Add(Ui.Num("Область выборки, мм", _scene.SampleRadiusMm, v => { _scene.SampleRadiusMm = Math.Max(1, v); Touch(); }));
        panel.Children.Add(Ui.Note("Сколько экрана усредняет один диод. Мало — дёргается на любом движении, много — всё сливается в один бурый цвет."));

        panel.Children.Add(Ui.Header("Коррекция"));
        panel.Children.Add(Ui.Slide("Яркость", _scene.Brightness, 0, 1, 0.01, v => { _scene.Brightness = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Насыщенность", _scene.Saturation, 0, 3, 0.05, v => { _scene.Saturation = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Гамма", _scene.Gamma, 0.5, 4, 0.05, v => { _scene.Gamma = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Температура", _scene.TemperatureK, 1500, 15000, 100, v => { _scene.TemperatureK = (int)v; Touch(); }, " K"));
        panel.Children.Add(Ui.Slide("Порог темноты", _scene.MinLuma, 0, 0.5, 0.01, v => { _scene.MinLuma = v; Touch(); }));
        panel.Children.Add(Ui.Note("Ниже этой яркости диод гаснет совсем, чтобы почти чёрный экран не оставлял корпус тускло подсвеченным."));

        panel.Children.Add(Ui.Header("Баланс по каналам"));
        panel.Children.Add(Ui.Slide("Красный", _scene.GainR, 0, 2, 0.01, v => { _scene.GainR = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Зелёный", _scene.GainG, 0, 2, 0.01, v => { _scene.GainG = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Синий", _scene.GainB, 0, 2, 0.01, v => { _scene.GainB = v; Touch(); }));
        panel.Children.Add(Ui.Note("Диоды на плате, в памяти и на вертушках по цвету заметно разные; это общий подгон под то, что видит глаз."));

        panel.Children.Add(Ui.Header("Плавность"));
        panel.Children.Add(Ui.Slide("Разгорается", _scene.SmoothingRise, 0.01, 1, 0.01, v => { _scene.SmoothingRise = v; Touch(); }));
        panel.Children.Add(Ui.Slide("Гаснет", _scene.SmoothingFall, 0.01, 1, 0.01, v => { _scene.SmoothingFall = v; Touch(); }));
        panel.Children.Add(Ui.Note("Больше — быстрее. Свет привычнее выглядит, когда нарастает резко, а спадает плавно."));

        // ---- тест
        panel.Children.Add(Ui.Header("Тест цветов и размещения"));
        panel.Children.Add(Ui.Note("Вместо экрана — одно пятно, которое таскаешь мышью по холсту. " +
                                   "Всё вне пятна чёрное, внутри — выбранный цвет, пропущенный через те же настройки. " +
                                   "Это единственный способ честно проверить, что светится именно то, что должно."));

        _testButton = Ui.Btn(_painter.TestActive ? "Завершить тест" : "Запустить тест", ToggleTest);
        panel.Children.Add(Ui.Row(_testButton));

        var shapeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        shapeBox.Items.Add("Круг");
        shapeBox.Items.Add("Квадрат");
        shapeBox.SelectedIndex = _scene.TestShape == TestShape.Circle ? 0 : 1;
        shapeBox.SelectionChanged += (_, _) =>
        {
            _scene.TestShape = shapeBox.SelectedIndex == 0 ? TestShape.Circle : TestShape.Square;
            _view.TestShape = _scene.TestShape;
            PushTestPatch();
            Touch();
        };
        panel.Children.Add(Ui.Labelled("Форма пятна", shapeBox));

        panel.Children.Add(Ui.Slide("Размер пятна", _scene.TestSizeMm, 20, 1200, 10, v =>
        {
            _scene.TestSizeMm = v;
            _view.TestSizeMm = v;
            _view.InvalidateVisual();
            PushTestPatch();
            Touch();
        }, " мм"));

        var swatch = new Border
        {
            Width = 28,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Ui.FgDim,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(ParseColor(_scene.TestColor))
        };

        panel.Children.Add(Ui.Row(swatch, Ui.Btn("Выбрать цвет…", () => PickTestColor(swatch))));
    });

    static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.OrangeRed; }
    }

    void PickTestColor(Border swatch)
    {
        var current = ParseColor(_scene.TestColor);

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var picked = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        _scene.TestColor = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";

        swatch.Background = new SolidColorBrush(picked);
        _view.TestColor = picked;
        _view.InvalidateVisual();

        PushTestPatch();
        Touch();
    }

    void ToggleTest()
    {
        if (_painter.TestActive) StopTest();
        else StartTest();
    }

    void StartTest()
    {
        if (!_hub.Connect(force: true)) { Say(_hub.Status); return; }

        // Everything goes dark first: whatever the case was showing a moment ago would
        // otherwise linger and be mistaken for the test's own output.
        _painter.Stop();
        _hub.Blackout();

        _view.TestMode = true;
        _view.TestShape = _scene.TestShape;
        _view.TestSizeMm = _scene.TestSizeMm;
        _view.TestColor = ParseColor(_scene.TestColor);
        _view.TestCenter = new Point(_scene.Monitor.CenterX, _scene.Monitor.CenterY);
        _view.InvalidateVisual();

        PushTestPatch();

        _painter.UseScene(_scene);
        _painter.Start();

        if (_testButton != null) _testButton.Content = "Завершить тест";
        Say("Тест идёт: таскай пятно по холсту и смотри, что загорается в корпусе.");
    }

    void StopTest()
    {
        if (!_view.TestMode && !_painter.TestActive) return;

        _view.TestMode = false;
        _view.InvalidateVisual();

        _painter.SetTest(null);
        _painter.Stop();

        if (_testButton != null) _testButton.Content = "Запустить тест";
        Say("Тест завершён, подсветка погашена.");
    }

    /// <summary>Hands the painter the patch as it stands right now.</summary>
    void PushTestPatch()
    {
        if (!_view.TestMode) return;

        var c = ParseColor(_scene.TestColor);
        _painter.SetTest(new TestPatch
        {
            CenterX = _view.TestCenter.X,
            CenterY = _view.TestCenter.Y,
            SizeMm = _scene.TestSizeMm,
            Circle = _scene.TestShape == TestShape.Circle,
            R = c.R,
            G = c.G,
            B = c.B
        });
    }
}
