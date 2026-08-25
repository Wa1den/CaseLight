using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CaseLight.Model;
using CaseLight.Render;

using CaseLight.Core.Text;

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
            (Fixture.Enabled ? "" : Loc.P("· выкл · ", "· off · ")) + $"{Fixture.Name}  ({Fixture.LedCount})";
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
            Name = Loc.P("Фигура ", "Fixture ") + (_scene.Fixtures.Count + 1),
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
        copy.Name = _view.Selected.Name + Loc.P(" (копия)", " (copy)");
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
            Content = new TextBlock { Text = "\uE711", FontFamily = Ui.IconFont, FontSize = 12 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            ToolTip = Loc.T("fixture.close")
        };
        close.Click += (_, _) => HideFixturePanel();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock
        {
            Text = Loc.T("fixture.head"),
            Foreground = Ui.Fg,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        _fixturePanel.Children.Add(head);

        _fixturePanel.Children.Add(Ui.Text(Loc.T("fixture.name"), f.Name, v => { f.Name = v; SyncFixtureList(); Touch(); }));
        _fixturePanel.Children.Add(Ui.Check(Loc.T("fixture.enabled"), f.Enabled, v => { f.Enabled = v; SyncFixtureList(); Touch(); }));
        _fixturePanel.Children.Add(Ui.IntBox(Loc.T("fixture.every"), f.UpdateEvery, v => { f.UpdateEvery = Math.Max(1, v); Touch(); },
            Loc.T("fixture.every.note")));

        _fixturePanel.Children.Add(Ui.Row(Ui.Btn(Loc.T("fixture.locate"), HighlightSelected)));

        // ---- привязка
        _fixturePanel.Children.Add(Ui.Header(Loc.T("fixture.binding")));

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
        _fixturePanel.Children.Add(Ui.Labeled(Loc.T("fixture.device"), deviceBox));

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
            _fixturePanel.Children.Add(Ui.Labeled(Loc.T("fixture.zone"), zoneBox));
        }
        else
        {
            _fixturePanel.Children.Add(Ui.Note(string.Format(Loc.T("fixture.missing"), f.Binding.DeviceName)));
        }

        _fixturePanel.Children.Add(Ui.IntBox(Loc.T("fixture.first"), f.Binding.FirstLed, v => { f.Binding.FirstLed = Math.Max(0, v); Touch(); }));
        _fixturePanel.Children.Add(Ui.IntBox(Loc.T("fixture.count"), f.Binding.LedCount, v => { f.Binding.LedCount = Math.Max(0, v); Touch(); },
            Loc.T("fixture.count.note")));

        // ---- место
        _fixturePanel.Children.Add(Ui.Header(Loc.T("fixture.place")));
        _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.x"), f.CenterX, v => { f.CenterX = v; Touch(); }));
        _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.y"), f.CenterY, v => { f.CenterY = v; Touch(); }));
        // Only the dimensions the arrangement actually has. Across a strip, or across a
        // ring standing edge-on, the fixture is as wide as the sampling area covers, and a
        // field for it would be a number that changes nothing.
        if (f.Arrangement == Arrangement.Strip)
        {
            _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.length"), f.Width, v => { f.Width = Math.Max(5, v); Touch(); },
                Loc.T("fixture.length.strip.note")));
        }
        else if (f.Arrangement == Arrangement.Closed && f.EdgeOn)
        {
            _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.length"), f.Height, v => { f.Height = Math.Max(5, v); Touch(); },
                Loc.T("fixture.length.ring.note")));
        }
        else if (f.Arrangement != Arrangement.Point)
        {
            _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.width"), f.Width, v => { f.Width = Math.Max(5, v); Touch(); }));
            _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.height"), f.Height, v => { f.Height = Math.Max(5, v); Touch(); }));
        }
        _fixturePanel.Children.Add(Ui.NumBox(Loc.T("fixture.rotation"), f.AngleDeg, v => { f.AngleDeg = v; Touch(); }));

        // ---- раскладка
        _fixturePanel.Children.Add(Ui.Header(Loc.T("fixture.arrangement")));

        var kindBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        kindBox.Items.Add(Loc.T("fixture.arr.strip"));
        kindBox.Items.Add(Loc.T("fixture.arr.closed"));
        kindBox.Items.Add(Loc.T("fixture.arr.point"));
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
        _fixturePanel.Children.Add(Ui.Labeled(Loc.T("fixture.shape"), kindBox));

        if (f.Arrangement == Arrangement.Closed)
        {
            _fixturePanel.Children.Add(Ui.Check(Loc.T("fixture.round"), f.RoundContour, v => { f.RoundContour = v; BuildFixturePanel(); Touch(); }));

            if (!f.RoundContour)
                _fixturePanel.Children.Add(Ui.Slider(Loc.T("fixture.aspect"), AspectToScale(f.ContourAspect), -10, 10, 0.1,
                    v => { f.ContourAspect = ScaleToAspect(v); Touch(); }, "",
                    Loc.T("fixture.aspect.note"),
                    format: DescribeAspect));
        }

        if (f.Arrangement != Arrangement.Point)
        {
            _fixturePanel.Children.Add(AnchorRow(f));
            _fixturePanel.Children.Add(Ui.Check(Loc.T("fixture.reverse"), f.Reverse, v => { f.Reverse = v; Touch(); }));
        }

        if (f.Arrangement == Arrangement.Closed)
        {
            _fixturePanel.Children.Add(Ui.Check(Loc.T("fixture.edgeon"), f.EdgeOn, v => { f.EdgeOn = v; BuildFixturePanel(); Touch(); },
                Loc.T("fixture.edgeon.note")));
        }
    }

    /// <summary>
    /// The anchor with its own stepper and a button that lights just that LED - the only
    /// reliable way to find which one is physically at the bottom is to look at it.
    /// </summary>
    UIElement AnchorRow(Fixture f)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(Ui.Caption(Loc.T("fixture.anchor"),
            Loc.T("fixture.anchor.note")));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        var value = new TextBlock
        {
            Text = f.AnchorLed.ToString(),
            Foreground = Ui.Fg,
            FontSize = 16,
            Width = 46,
            TextAlignment = TextAlignment.Center,
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
        row.Children.Add(Ui.Btn(Loc.T("fixture.show"), () => ShowAnchor(f)));

        panel.Children.Add(row);
        return panel;
    }

    void ShowAnchor(Fixture f)
    {
        if (_painter.IsRunning)
        {
            Say(Loc.P("Раскраска перезапишет подсветку. Остановите её перед проверкой.", "The painting will overwrite the lighting. Stop it before checking."));
            return;
        }

        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _hub.HighlightLed(f, f.AnchorLed, 255, 60, 0);
        Say(string.Format(Loc.P("Горит только диод {0}, он считается начальным.",
                                "Only LED {0} is lit, the one taken as the first."), f.AnchorLed));
    }

    void HighlightSelected()
    {
        if (_view.Selected == null) { Say(Loc.P("Фигура не выбрана.", "No fixture selected.")); return; }

        if (_painter.IsRunning)
        {
            Say(Loc.P("Раскраска перезапишет подсветку. Остановите её перед проверкой.", "The painting will overwrite the lighting. Stop it before checking."));
            return;
        }

        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _hub.Highlight(_view.Selected, 255, 255, 255);
        Say(string.Format(Loc.P("Горит только «{0}».", "Only «{0}» is lit."), _view.Selected.Name));
    }

    // ---- вкладка цветов и тест размещения ---------------------------------

    void BuildColorsSection() => AddSection(Loc.T("tab.color"), "\uE790", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("color.head")));
        panel.Children.Add(Ui.Slider(Loc.T("color.brightness"), _scene.Brightness, 0, 1, 0.01, v => { _scene.Brightness = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.saturation"), _scene.Saturation, 0, 3, 0.05, v => { _scene.Saturation = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.gamma"), _scene.Gamma, 0.5, 4, 0.05, v => { _scene.Gamma = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.temperature"), _scene.TemperatureK, 1500, 15000, 100, v => { _scene.TemperatureK = (int)v; Touch(); }, " K"));
        // Кубическая шкала: рабочие значения лежат около 0,003, и на линейной шкале весь ход
        // уходит на ту часть диапазона, где лента просто гаснет.
        panel.Children.Add(Ui.Slider(Loc.T("color.minluma"), Math.Pow(_scene.MinLuma / 0.3, 1.0 / 3.0), 0, 1, 0.005,
            v => { _scene.MinLuma = Math.Pow(v, 3) * 0.3; Touch(); }, "",
            Loc.T("color.minluma.note"),
            format: v => v <= 0 ? Loc.T("off")
                                : (Math.Pow(v, 3) * 0.3).ToString("0.0000", CultureInfo.InvariantCulture)));

        panel.Children.Add(Ui.Slider(Loc.T("color.shadow"), _scene.ShadowNeutral, 0, 0.4, 0.01,
            v => { _scene.ShadowNeutral = v; Touch(); }, "",
            Loc.T("color.shadow.note"),
            format: v => v <= 0 ? Loc.T("off") : v.ToString("0.##", CultureInfo.InvariantCulture)));

        panel.Children.Add(Ui.Header(Loc.T("color.gains"),
            Loc.T("color.gains.note")));
        panel.Children.Add(Ui.Slider(Loc.T("color.red"), _scene.GainR, 0, 2, 0.01, v => { _scene.GainR = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.green"), _scene.GainG, 0, 2, 0.01, v => { _scene.GainG = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.blue"), _scene.GainB, 0, 2, 0.01, v => { _scene.GainB = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("color.smoothing"),
            Loc.T("color.smoothing.note")));
        panel.Children.Add(Ui.Slider(Loc.T("color.rise"), _scene.SmoothingRise, 0.01, 1, 0.01, v => { _scene.SmoothingRise = v; Touch(); }));
        panel.Children.Add(Ui.Slider(Loc.T("color.fall"), _scene.SmoothingFall, 0.01, 1, 0.01, v => { _scene.SmoothingFall = v; Touch(); }));
    });

    /// <summary>
    /// The placement test, in a section of its own.
    ///
    /// It shares nothing with the colour settings except the pipeline it feeds: the point
    /// here is which fixture lights up, not what shade it lights up in.
    /// </summary>
    void BuildTestSection() => AddSection(Loc.T("tab.test"), "\uE890", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("tab.test"),
            Loc.T("test.note")));

        _testButton = Ui.Btn(_painter.TestActive ? Loc.T("test.stop") : Loc.T("test.start"), ToggleTest, accent: true);
        panel.Children.Add(Ui.Row(_testButton));

        var shapeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        shapeBox.Items.Add(Loc.T("test.circle"));
        shapeBox.Items.Add(Loc.T("test.square"));
        shapeBox.SelectedIndex = _scene.TestShape == TestShape.Circle ? 0 : 1;
        shapeBox.SelectionChanged += (_, _) =>
        {
            _scene.TestShape = shapeBox.SelectedIndex == 0 ? TestShape.Circle : TestShape.Square;
            _view.TestShape = _scene.TestShape;
            PushTestPatch();
            Touch();
        };
        panel.Children.Add(Ui.Labeled(Loc.T("test.shape"), shapeBox));

        panel.Children.Add(Ui.Slider(Loc.T("test.size"), _scene.TestSizeMm, 20, 1200, 10, v =>
        {
            _scene.TestSizeMm = v;
            _view.TestSizeMm = v;
            _view.InvalidateVisual();
            PushTestPatch();
            Touch();
        }, Loc.T("unit.mm")));

        var swatch = new Border
        {
            Width = 28,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Ui.PanelStroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(ParseColor(_scene.TestColor))
        };

        panel.Children.Add(Ui.Row(swatch, Ui.Btn(Loc.T("test.colour"), () => PickTestColor(swatch))));
    });

    /// <summary>
    /// The contour aspect as a signed number: taller than wide reads positive, wider than
    /// tall reads negative.
    ///
    /// Stored it is height over width, where a frame three times wider is 0.33 - a number
    /// nobody reads as "three times wider". The sign says which way round, the size says by
    /// how much, and one is the square in the middle.
    /// </summary>
    static double AspectToScale(double aspect) =>
        aspect >= 1 ? Math.Min(10, aspect) : -Math.Min(10, 1 / Math.Max(0.0001, aspect));

    static double ScaleToAspect(double scale) =>
        scale >= 1 ? scale
        : scale <= -1 ? 1 / -scale
        : 1;

    static string DescribeAspect(double scale) =>
        scale >= 1.05 ? string.Format(Loc.T("fixture.taller"), scale.ToString("0.#", CultureInfo.InvariantCulture))
        : scale <= -1.05 ? string.Format(Loc.T("fixture.wider"), (-scale).ToString("0.#", CultureInfo.InvariantCulture))
        : Loc.T("fixture.square");

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
        EnsureServer();
        if (!_hub.Connect()) { Say(_hub.Status); return; }

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

        if (_testButton != null) _testButton.Content = Loc.T("test.stop");
        Say(Loc.P("Тест запущен. Пятно перемещается мышью по холсту.", "Test running. The patch is moved around the canvas with the mouse."));
    }

    void StopTest()
    {
        if (!_view.TestMode && !_painter.TestActive) return;

        // The test drives the painter too, but nobody wants it brought back by a recovery
        _paintingWanted = false;

        _view.TestMode = false;
        _view.InvalidateVisual();

        _painter.SetTest(null);
        _painter.Stop();

        if (_testButton != null) _testButton.Content = Loc.T("test.start");
        Say(Loc.P("Тест завершён, подсветка погашена.", "Test finished, the lighting is off."));
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
