using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CaseLight.Core.Capture;
using CaseLight.Core.Power;
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
public sealed partial class MainWindow : Window
{
    readonly RgbHub _hub = new();
    readonly SceneView _view = new();
    readonly PowerWatcher _power = new();
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(500) };

    Scene _scene = Scene.Load();
    Scene _saved = null!;
    CasePainter _painter = null!;

    System.Windows.Forms.NotifyIcon? _tray;

    ListBox _nav = null!;
    ContentControl _pageHost = null!;
    readonly List<UIElement> _pages = new();
    Border _dirtyBar = null!;
    Border _fixtureOverlay = null!;
    StackPanel _fixturePanel = null!;
    ListBox _fixtureList = null!;
    TextBlock _status = null!;
    TextBlock[] _statValues = System.Array.Empty<TextBlock>();
    Button _testButton = null!;

    bool _rebuildingUi;
    bool _syncingList;

    /// <summary>
    /// Whether painting is meant to be on, as opposed to whether the thread happens to be
    /// alive right now.
    ///
    /// Recovery used to restart the painting only if the thread was running when it began.
    /// If the painting had already died - the server took it down with it - the restart
    /// brought the connection back and left the case dark until someone pressed Start.
    /// </summary>
    bool _paintingWanted;

    /// <summary>
    /// Set only by the ways out that really mean it: the tray menu and a Windows shutdown.
    ///
    /// With the tray enabled the close button hides the window instead of ending the
    /// program, so without this flag there would be no way left to quit at all.
    /// </summary>
    bool _reallyClosing;

    /// <summary>When we last launched the server, so the wait can be reported honestly.</summary>
    long _serverStartedTicks;

    /// <summary>Guards against running the wake recovery twice for one wake.</summary>
    bool _wokeUp;

    /// <summary>Throttles re-reading the list while the server is still detecting.</summary>
    long _lastListPoll;

    /// <summary>
    /// How many more times the controller list is re-read before it is believed.
    ///
    /// The port opens before detection has finished, and the list arrives in pieces - the
    /// memory on the SMBus turned up first here, the rest several seconds later. Trusting
    /// the first non-empty list is how a layout ends up bound to one controller out of
    /// three, with every other fixture reporting that its device is not visible.
    /// </summary>
    int _settlePolls;

    /// <summary>Reads without a change after which the list is taken as final.</summary>
    const int SettlePolls = 4;

    /// <summary>
    /// Recovery owns the hub while it runs.
    ///
    /// It disposes the client and restarts the server from a background thread, and the UI
    /// tick reconnects on its own every half second - without this flag the two would be
    /// taking the same connection apart and putting it back together at once.
    /// </summary>
    volatile bool _recovering;

    public MainWindow()
    {
        Title = "CaseLight — подсветка корпуса";

        ProbeLog.Configure(Scene.LogPath, _scene.WriteLog);
        CaseLight.Core.Text.Loc.Configure(System.IO.Path.Combine(Scene.Folder, "lang"));

        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/icon.ico")); }
        catch { /* без иконки окно всё равно работает */ }

        _saved = _scene.Clone();
        _painter = new CasePainter(_hub, _scene);

        RestoreWindowGeometry();
        Content = BuildLayout();

        _view.Scene = _scene;
        _view.SelectionChanged += (_, _) => { SyncFixtureList(); ShowFixturePanel(); };
        // Live while the mouse moves: the painter only sets a flag and rebuilds its zones
        // once per frame anyway, so the case follows the fixture as it is dragged.
        _view.FixtureChanged += (_, _) => _painter.Invalidate();

        // The expensive half waits for the mouse to come up. Dragging a fixture edits the
        // scene exactly as typing a coordinate does, so the pending-changes bar has to say
        // so - but rebuilding the panel and serialising the scene are not worth doing a
        // hundred times for one gesture.
        _view.FixtureEdited += (_, _) => { BuildFixturePanel(); Touch(); };
        _view.TestMoved += (_, _) => PushTestPatch();

        HookPower();

        Loaded += (_, _) =>
        {
            _power.Attach(this);
            SetupTray();

            EnsureServer();
            ConnectHub();
            RebuildSections();
            SyncFixtureList();
            _view.FitToContent();

            if (_scene.StartMinimized) WindowState = WindowState.Minimized;
            if (_scene.StartPaintingOnLaunch) StartPainting();
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _scene.MinimizeToTray) Hide();
        };

        _ui.Tick += (_, _) => RefreshUi();
        _ui.Start();

        // a Windows shutdown must not be cancelled into the tray
        Application.Current.SessionEnding += (_, _) => _reallyClosing = true;

        Closing += (_, e) =>
        {
            if (!_reallyClosing && _scene.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            SaveWindowGeometry();

            if (_scene.OffOnExit) _hub.Blackout();

            // Geometry is not something the user is editing, so it persists on its own -
            // written onto the last applied state so pending edits stay discarded.
            _saved.WindowWidth = _scene.WindowWidth;
            _saved.WindowHeight = _scene.WindowHeight;
            _saved.WindowLeft = _scene.WindowLeft;
            _saved.WindowTop = _scene.WindowTop;
            _saved.WindowMaximized = _scene.WindowMaximized;
            _saved.Save();

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _painter.Dispose();
            _power.Dispose();
            _hub.Dispose();
        };
    }

    // ---- каркас -----------------------------------------------------------

    UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- слева: столбец разделов шириной по самой длинной подписи
        _nav = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 12, 6, 12)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_nav, ScrollBarVisibility.Disabled);

        // Свой отступ у пунктов: стандартный тесноват для строки со значком. Стиль без
        // BasedOn не отменяет тему - шаблон по-прежнему приходит из неё.
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        _nav.ItemContainerStyle = itemStyle;

        _nav.SelectionChanged += (_, _) =>
        {
            if (_rebuildingUi) return;

            int i = _nav.SelectedIndex;
            if (i < 0 || i >= _pages.Count) return;

            _pageHost.Content = _pages[i];

            // The fixture panel belongs to one section only; leaving it over the canvas
            // while looking at, say, power settings is just clutter.
            HideFixturePanel();
        };

        Grid.SetColumn(_nav, 0);
        grid.Children.Add(_nav);

        // ---- по центру: страница выбранного раздела и полоса применения
        var page = new Grid { Margin = new Thickness(0, 12, 12, 12) };
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _pageHost = new ContentControl();
        Grid.SetRow(_pageHost, 0);
        page.Children.Add(_pageHost);

        _dirtyBar = BuildDirtyBar();
        Grid.SetRow(_dirtyBar, 1);
        page.Children.Add(_dirtyBar);

        Grid.SetColumn(page, 1);
        grid.Children.Add(page);

        // ---- справа: холст и панель фигуры поверх него
        var right = new Grid { Margin = new Thickness(0, 12, 12, 12) };
        right.Children.Add(_view);

        // The Fluent scroll bar is drawn over the content instead of taking a column of
        // its own, so the panel keeps a margin wide enough for it to land on.
        _fixturePanel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        _fixtureOverlay = Ui.Card(new ScrollViewer
        {
            Content = _fixturePanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        _fixtureOverlay.Background = Ui.PanelSolid;
        _fixtureOverlay.Width = 340;
        _fixtureOverlay.Margin = new Thickness(0, 10, 10, 10);
        _fixtureOverlay.HorizontalAlignment = HorizontalAlignment.Right;
        _fixtureOverlay.VerticalAlignment = VerticalAlignment.Stretch;
        _fixtureOverlay.Visibility = Visibility.Collapsed;
        right.Children.Add(_fixtureOverlay);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        // ---- низ: действия и статус
        var bottom = new StackPanel { Margin = new Thickness(12, 0, 12, 12), Orientation = Orientation.Horizontal };
        bottom.Children.Add(Ui.Btn("Старт", StartPainting));
        bottom.Children.Add(Ui.Btn("Стоп", StopPainting));
        bottom.Children.Add(Ui.Btn("Переподключиться", ConnectHub));
        bottom.Children.Add(Ui.Btn("Центрировать холст", () => _view.FitToContent()));

        _status = new TextBlock
        {
            Foreground = Ui.FgDim,
            FontSize = Ui.TextSize,
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

    Border BuildDirtyBar()
    {
        var apply = Ui.Btn("Применить", ApplyChanges, accent: true);
        var cancel = Ui.Btn("Отмена", CancelChanges);

        var dock = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(apply, Dock.Right);
        dock.Children.Add(cancel);
        dock.Children.Add(apply);
        dock.Children.Add(new TextBlock
        {
            Text = "Есть несохранённые изменения",
            Foreground = Ui.Warn,
            FontSize = Ui.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        var card = Ui.Card(dock);
        card.Padding = new Thickness(12);
        card.Margin = new Thickness(0, 10, 0, 0);
        card.Visibility = Visibility.Collapsed;
        return card;
    }

    // ---- разделы ----------------------------------------------------------

    /// <summary>
    /// One section: a page of settings on a card, plus its row in the left-hand list.
    ///
    /// The page carries no heading of its own - which section is open is already visible
    /// in the list, and repeating it costs a line at the top of every page.
    /// </summary>
    void AddSection(string title, string glyph, Action<StackPanel> build)
    {
        var panel = new StackPanel();
        build(panel);

        AddSection(title, glyph, new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Ui.Card(panel)
        });
    }

    /// <summary>
    /// A section whose page brings its own layout.
    ///
    /// The usual page is a column of controls that scrolls when it runs long. A page built
    /// around a list wants the opposite - the list should take the height that is going
    /// spare - and that cannot be said in a stack.
    /// </summary>
    void AddSection(string title, string glyph, UIElement page)
    {
        _pages.Add(page);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = Ui.IconFont,
            FontSize = 16,
            Foreground = Ui.Fg,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Ui.TextSize,
            Foreground = Ui.Fg,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        _nav.Items.Add(new ListBoxItem { Content = row });
    }

    /// <summary>Rebuilt wholesale after Cancel or import, since every field may have moved.</summary>
    void RebuildSections()
    {
        _rebuildingUi = true;

        int selected = Math.Max(0, _nav.SelectedIndex);

        _nav.Items.Clear();
        _pages.Clear();

        BuildGeneralSection();
        BuildDevicesSection();
        BuildCaptureSection();
        BuildColorsSection();
        BuildTestSection();
        BuildPowerSection();
        BuildAboutSection();

        selected = Math.Min(selected, _pages.Count - 1);

        // The selection change arrives while the rebuild guard is still up, so the page is
        // handed over here rather than left to the handler.
        _nav.SelectedIndex = selected;
        _pageHost.Content = _pages[selected];

        _rebuildingUi = false;
    }

    void BuildGeneralSection() => AddSection("Основное", "\uE713", panel =>
    {
        panel.Children.Add(Ui.Header("Окно"));
        panel.Children.Add(Ui.Check("Сворачивать в трей", _scene.MinimizeToTray, v => { _scene.MinimizeToTray = v; Touch(); },
            "Крестик прячет окно, программа продолжает работать. Выход - через меню значка в трее."));
        panel.Children.Add(Ui.Check("Запускать свёрнутым", _scene.StartMinimized, v => { _scene.StartMinimized = v; Touch(); }));

        panel.Children.Add(Ui.Header("Запуск"));
        panel.Children.Add(Ui.Check("Запускать вместе с Windows", Autostart.IsEnabled(), v => Say(Autostart.Set(v)),
            "Подсветкой управляет OpenRGB, поэтому автозапуск CaseLight имеет смысл только вместе с автозапуском OpenRGB."));
        panel.Children.Add(Ui.Check("Сразу начинать раскраску", _scene.StartPaintingOnLaunch, v => { _scene.StartPaintingOnLaunch = v; Touch(); }));

        panel.Children.Add(Ui.Header("Сервер OpenRGB"));
        panel.Children.Add(Ui.Check("Запускать OpenRGB, если он не запущен", _scene.AutoStartOpenRgb, v => { _scene.AutoStartOpenRgb = v; Touch(); },
            "Сервер запускается с ключами --server --startminimized: первый открывает порт 6742, второй убирает окно."));
        panel.Children.Add(Ui.Check("Запускать от администратора", _scene.OpenRgbAsAdmin, v => { _scene.OpenRgbAsAdmin = v; Touch(); },
            "Права требуются для оперативной памяти: она на шине SMBus, и без прав OpenRGB её вовсе не находит — модули остаются в своём режиме и после перезагрузки светят радугой. " +
            "Запуск отсюда спрашивает UAC каждый раз; чтобы этого не было, есть задание в планировщике ниже."));

        panel.Children.Add(Ui.Check("Запускать при входе от администратора", OpenRgbTask.Exists(), SetLogonTask,
            "Задание в планировщике: сервер стартует при входе в систему сразу с правами, и UAC больше не появляется. " +
            "Запрос прав будет один раз, при создании задания. Повышается только сервер OpenRGB, само приложение работает без прав."));

        // Shown, not stored: the setting stays empty so the search runs again if OpenRGB
        // ever moves, while the field says which file that search lands on today.
        string knownPath = string.IsNullOrWhiteSpace(_scene.OpenRgbPath)
            ? OpenRgbLauncher.FindExe() ?? ""
            : _scene.OpenRgbPath;

        panel.Children.Add(Ui.Text("Путь к OpenRGB.exe", knownPath, v => { _scene.OpenRgbPath = v; Touch(); },
            "Заполняется найденным файлом. Пустое поле - искать заново при каждом запуске: сначала в Program Files и %LocalAppData%\\Programs, затем в записях об удалении в реестре."));
        panel.Children.Add(Ui.Row(Ui.Btn("Найти", () =>
        {
            string? found = OpenRgbLauncher.FindExe();
            Say(found == null ? "OpenRGB.exe не найден, укажите путь вручную" : "Найден: " + found);
        }), Ui.Btn("Запустить сейчас", () => Say(OpenRgbLauncher.Launch(
            string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath, _scene.OpenRgbAsAdmin)))));

        panel.Children.Add(Ui.Header("Настройки",
            "Один файл со всем: раскладка, размеры монитора, цвета, захват, питание."));
        panel.Children.Add(Ui.Row(Ui.Btn("Экспорт…", ExportSettings), Ui.Btn("Импорт…", ImportSettings)));

        panel.Children.Add(Ui.Header("Журнал"));
        panel.Children.Add(Ui.Check("Вести журнал", _scene.WriteLog, v =>
        {
            _scene.WriteLog = v;
            ProbeLog.Configure(Scene.LogPath, v);
            Touch();
        }));
        panel.Children.Add(Ui.Note(Scene.LogPath));
    });

    void BuildDevicesSection()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = Ui.Header("Фигуры",
            "Одна фигура на каждое светящееся устройство. Параметры выбранной фигуры открываются панелью поверх холста.");
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        _fixtureList = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
        _fixtureList.SelectionChanged += (_, _) =>
        {
            if (_syncingList) return;
            _view.Select((_fixtureList.SelectedItem as FixtureItem)?.Fixture);
        };
        Grid.SetRow(_fixtureList, 1);
        grid.Children.Add(_fixtureList);

        var buttons = Ui.Row(
            Ui.Btn("Добавить", AddFixture),
            Ui.Btn("Копия", DuplicateFixture),
            Ui.Btn("Удалить", RemoveFixture));
        buttons.Margin = new Thickness(0, 0, 0, 0);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        var showDisabled = Ui.Check("Отображать отключённые", _scene.ShowDisabled,
            v => { _scene.ShowDisabled = v; Touch(); },
            "Фигуры, снятые с раскраски, перестают рисоваться на холсте. Выбранная фигура рисуется в любом случае.");
        Grid.SetRow(showDisabled, 3);
        grid.Children.Add(showDisabled);

        AddSection("Устройства", "\uE772", Ui.Card(grid));
        SyncFixtureList();
    }

    void BuildCaptureSection() => AddSection("Захват", "\uE7F4", panel =>
    {
        panel.Children.Add(Ui.Header("Источник кадров"));

        var box = new ComboBox { Margin = new Thickness(0, 2, 0, 0) };
        box.Items.Add("Получать от Rimlight");
        box.Items.Add("Свой захват: автоматически");
        box.Items.Add("Свой захват: только DDA");
        box.Items.Add("Свой захват: только WGC");
        box.Items.Add("Свой захват: только GDI");
        box.SelectedIndex = (int)_scene.CaptureSource;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            _scene.CaptureSource = (CaptureSource)box.SelectedIndex;
            RebuildSections();
            Touch();
        };

        panel.Children.Add(Ui.Labelled("Метод", box,
            "От Rimlight кадры приходят через разделяемую память, свой захват при этом не работает. " +
            "«Автоматически» держит DDA и WGC вместе и переходит на GDI, когда те перестают выдавать кадры."));

        // ---- экран
        bool ourCapture = _scene.CaptureSource != CaptureSource.FromRimlight;
        var monitors = Native.EnumerateMonitors();

        var monitorBox = new ComboBox { Margin = new Thickness(0, 2, 0, 0), IsEnabled = ourCapture };
        foreach (var m in monitors) monitorBox.Items.Add(m.ToString());

        int index = monitors.FindIndex(m => m.DeviceName == _scene.MonitorDeviceName);
        if (index < 0) index = monitors.FindIndex(m => m.IsPrimary);
        monitorBox.SelectedIndex = Math.Max(0, index);

        monitorBox.SelectionChanged += (_, _) =>
        {
            int i = monitorBox.SelectedIndex;
            if (i < 0 || i >= monitors.Count) return;

            _scene.MonitorDeviceName = monitors[i].DeviceName;
            AdoptScreen(monitors[i]);

            // the rectangle can change shape entirely - an ultrawide for a portrait screen -
            // so bring the whole layout back into view rather than leave it off the edge
            _view.FitToContent();

            RebuildSections();
            Touch();
        };

        panel.Children.Add(Ui.Labelled("Экран", monitorBox,
            "Монитор для захвата. При режиме получения от Rimlight выбор определяется им."));

        panel.Children.Add(Ui.Note($"Прямоугольник монитора: {_scene.Monitor.Width:F0} × {_scene.Monitor.Height:F0} мм"));

        panel.Children.Add(Ui.Slide("Кадров в секунду", _scene.MaxFps, 1, 120, 1, v => { _scene.MaxFps = (int)v; Touch(); }, "",
            "Верхний предел для быстрых устройств. Медленным задаётся свой делитель в параметрах фигуры."));

        panel.Children.Add(Ui.Num("Область выборки, мм", _scene.SampleRadiusMm, v => { _scene.SampleRadiusMm = Math.Max(1, v); Touch(); },
            "Размер участка экрана, усредняемого для одного диода. При малом значении цвет меняется от любого движения в кадре, при большом усредняется до однородного оттенка."));

        panel.Children.Add(Ui.Header("Статистика"));
        panel.Children.Add(BuildStats());
    });

    /// <summary>
    /// Takes the monitor rectangle from the screen itself.
    ///
    /// It used to be typed in by hand, which meant it was usually the size of some other
    /// monitor: the numbers are hard to look up and easy to leave stale. EDID knows them,
    /// so the only thing left to choose is which screen.
    /// </summary>
    void AdoptScreen(MonitorInfo monitor)
    {
        var (w, h) = DisplaySize.Rect(monitor, _scene.Monitor.Width);
        _scene.Monitor.Width = w;
        _scene.Monitor.Height = h;
        _view.InvalidateVisual();
    }

    static readonly string[] StatRows =
        { "Источник", "Состояние", "Кадры", "Частота", "Задержка", "Диодов", "OpenRGB" };

    /// <summary>
    /// The statistics as a two-column table.
    ///
    /// It used to be one monospaced block padded with spaces, which fell apart the moment a
    /// value grew longer than its column. A grid lines the values up on its own and lets
    /// the long ones wrap.
    /// </summary>
    UIElement BuildStats()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _statValues = new TextBlock[StatRows.Length];

        for (int i = 0; i < StatRows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = StatRows[i],
                Foreground = Ui.FgDim,
                FontSize = Ui.TextSize,
                Margin = new Thickness(0, 2, 14, 2)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var value = new TextBlock
            {
                Foreground = Ui.Fg,
                FontSize = Ui.TextSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            _statValues[i] = value;
        }

        return grid;
    }

    void BuildPowerSection() => AddSection("Питание", "\uE7E8", panel =>
    {
        panel.Children.Add(Ui.Header("Гасить подсветку"));
        panel.Children.Add(Ui.Check("при выходе из программы", _scene.OffOnExit, v => { _scene.OffOnExit = v; Touch(); }));
        panel.Children.Add(Ui.Check("когда экран выключен", _scene.OffOnDisplayOff, v => { _scene.OffOnDisplayOff = v; Touch(); }));
        panel.Children.Add(Ui.Check("при блокировке сессии", _scene.OffOnLock, v => { _scene.OffOnLock = v; Touch(); }));
        panel.Children.Add(Ui.Check("при уходе в сон", _scene.OffOnSuspend, v => { _scene.OffOnSuspend = v; Touch(); }));

        panel.Children.Add(Ui.Header("После пробуждения"));

        var wakeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        wakeBox.Items.Add("Ничего не делать");
        wakeBox.Items.Add("Перезапустить OpenRGB");
        wakeBox.SelectedIndex = Math.Max(0, Array.IndexOf(WakeModes, WakeMode));
        wakeBox.SelectionChanged += (_, _) =>
        {
            if (wakeBox.SelectedIndex < 0) return;
            _scene.WakeRecovery = WakeModes[wakeBox.SelectedIndex];
            Touch();
        };
        panel.Children.Add(Ui.Labelled("Что делать", wakeBox,
            "Во сне контроллеры переподключаются к USB, а работавший сервер продолжает запись в прежние дескрипторы и возвращает признак успеха: " +
            "подсветка при этом остаётся в состоянии, установленном при подаче питания. Перезапуск возвращает управление."));

        panel.Children.Add(Ui.Row(Ui.Btn("Перезапустить OpenRGB сейчас", RestartServerNow)));

        panel.Children.Add(Ui.Slide("Пауза перед возвратом", _scene.ResumeDelayMs / 1000.0, 0, 30, 1,
            v => { _scene.ResumeDelayMs = (int)(v * 1000); Touch(); }, " с",
            "Первая запись после пробуждения откладывается на это время. По журналу Windows сервер завершался аварийно через 41 секунду после выхода из сна, " +
            "пока устройства переподключались, а он держал прежние дескрипторы."));
    });

    /// <summary>
    /// The recovery modes offered. Rescanning is not among them: the request kills the
    /// server on this hardware, and with the restart no longer costing a rights prompt it
    /// had nothing left to offer. Settings that still name it are read as a restart.
    /// </summary>
    static readonly WakeRecovery[] WakeModes = { WakeRecovery.Nothing, WakeRecovery.RestartServer };

    WakeRecovery WakeMode =>
        _scene.WakeRecovery == WakeRecovery.Rescan ? WakeRecovery.RestartServer : _scene.WakeRecovery;

    void BuildAboutSection() => AddSection("О программе", "\uE897", panel =>
    {
        panel.Children.Add(Ui.Header("CaseLight " + AppVersion));

        panel.Children.Add(Ui.Note("Подсветка внутри корпуса воспроизводит изображение с экрана. Каждое светящееся устройство " +
                                   "описывается там, где оно физически стоит, и получает цвет с ближайшего к нему участка экрана."));

        panel.Children.Add(Ui.Note("Управление идёт через OpenRGB: устройства на ARGB-контроллере и оперативная память на шине SMBus. " +
                                   "Сервер запускается программой, если не запущен, и перезапускается после выхода из сна."));

        panel.Children.Add(Ui.Note("Кадры берутся собственным захватом или принимаются от Rimlight, подсветки монитора. " +
                                   "Rimlight не обязателен."));

        panel.Children.Add(Ui.Link("Репозиторий:", "https://github.com/Wa1den/CaseLight"));
        panel.Children.Add(Ui.Link("Rimlight, подсветка монитора:", "https://github.com/Wa1den/Rimlight"));
        panel.Children.Add(Ui.Link("OpenRGB:", "https://openrgb.org"));
    });

    /// <summary>
    /// Registers or removes the logon task, then rebuilds the page from what actually
    /// happened - the prompt can be declined, and the checkbox must not claim otherwise.
    /// </summary>
    void SetLogonTask(bool enabled)
    {
        if (_rebuildingUi) return;

        string path = string.IsNullOrWhiteSpace(_scene.OpenRgbPath)
            ? OpenRgbLauncher.FindExe() ?? ""
            : _scene.OpenRgbPath;

        Say(enabled ? OpenRgbTask.Create(path) : OpenRgbTask.Delete());
        RebuildSections();
    }

    /// <summary>Version from the assembly, so it can only be changed in one place.</summary>
    static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // ---- применить и отменить ---------------------------------------------

    /// <summary>Called after any edit; the bar appears only when something really differs.</summary>
    void Touch()
    {
        if (_rebuildingUi) return;

        _view.InvalidateVisual();
        _painter?.Invalidate();
        UpdateDirtyBar();
    }

    void UpdateDirtyBar()
    {
        bool dirty = _scene.DiffersFrom(_saved);
        _dirtyBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyChanges()
    {
        _saved = _scene.Clone();
        _saved.Save();
        UpdateDirtyBar();
        Say("Настройки применены и сохранены.");
    }

    void CancelChanges()
    {
        // CopyFrom keeps the object identity the painter and the canvas already hold, so
        // undoing a drag needs no rewiring - only a redraw.
        _scene.CopyFrom(_saved);

        _view.Select(null);
        RebuildSections();
        SyncFixtureList();
        _painter.Invalidate();
        _view.InvalidateVisual();
        UpdateDirtyBar();
        Say("Изменения отменены.");
    }

    void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт настроек CaseLight",
            Filter = "Настройки CaseLight (*.json)|*.json",
            FileName = "caselight-settings.json"
        };

        if (dialog.ShowDialog() != true) return;

        try { _scene.Save(dialog.FileName); Say("Экспортировано: " + dialog.FileName); }
        catch (Exception ex) { Say("Не удалось сохранить: " + ex.Message); }
    }

    void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт настроек CaseLight",
            Filter = "Настройки CaseLight (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var loaded = Scene.Import(dialog.FileName);
            _scene.CopyFrom(loaded);

            _view.Select(null);
            RebuildSections();
            SyncFixtureList();
            _painter.Invalidate();
            _view.FitToContent();
            UpdateDirtyBar();

            Say("Импортировано. Проверьте раскладку и нажмите «Применить».");
        }
        catch (Exception ex)
        {
            Say("Не удалось прочитать файл: " + ex.Message);
        }
    }

    // ---- питание, трей, статус --------------------------------------------

    void HookPower()
    {
        _power.Changed += (_, state) =>
        {
            string? reason =
                state.Suspended && _scene.OffOnSuspend ? "сон" :
                state.Locked && _scene.OffOnLock ? "блокировка" :
                state.DisplayOff && _scene.OffOnDisplayOff ? "экран выключен" :
                null;

            if (state.Suspended) _wokeUp = false;

            if (reason != null)
            {
                _painter.Pause(reason);
                return;
            }

            // Only a real wake needs the server shaken; unlocking the session does not.
            if (state is { Suspended: false } && _power.LastResumeTicks > 0 && !_wokeUp)
            {
                _wokeUp = true;
                RecoverAfterWake();
                return;
            }

            // No delay here. The pause before the first write is there because the buses
            // are still settling after a wake, and waking is handled above; this branch is
            // an unlock, a screen coming back, or the very first notification at startup.
            // Holding those for eight seconds only left the case dark for no reason.
            _painter.Resume(0);
        };
    }

    /// <summary>
    /// Brings the lighting back after sleep.
    ///
    /// Resuming alone is not enough: the controllers were re-enumerated while the machine
    /// slept, and the server that stayed up keeps writing into handles that lead nowhere -
    /// it reports success while the case shows its power-on pattern. Restarting the server
    /// is blunt but it is what actually works; the gentler rescan is offered because it
    /// sometimes suffices, though it is known to take the server down with it.
    /// </summary>
    void RecoverAfterWake()
    {
        var mode = WakeMode;

        if (mode == WakeRecovery.Nothing)
        {
            _painter.Resume(_scene.ResumeDelayMs);
            return;
        }

        _recovering = true;

        // Stop writing before touching the server: a restart would be writing into a dying
        // process.
        _painter.Pause("восстановление после сна");
        Say("Пробуждение: восстановление связи с OpenRGB.");

        System.Threading.Tasks.Task.Run(() =>
        {
            string what = "";
            bool back = false;

            try
            {
                // let the USB stack finish re-enumerating before anything is asked of it
                System.Threading.Thread.Sleep(Math.Max(2000, _scene.ResumeDelayMs));

                // The connection goes first: a restart would otherwise be writing into a
                // dying process.
                _hub.Dispose();

                what = OpenRgbLauncher.Restart(
                    string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath,
                    _scene.OpenRgbAsAdmin);

                back = WaitForDevices();
            }
            finally
            {
                // The flag comes down whatever happened. While it is up nothing reconnects
                // on its own and the reconnect button refuses to work, so a recovery that
                // ended badly used to leave the program deaf until it was restarted.
                _recovering = false;
            }

            Dispatcher.Invoke(() =>
            {
                Say(back
                    ? $"{what}; подсветка восстановлена"
                    : $"{what}; сервер не отвечает, нажмите «Переподключиться»");

                BuildFixturePanel();

                _painter.Resume(0);
                if (_paintingWanted && !_painter.IsRunning) _painter.Start();
            });
        });
    }

    void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "CaseLight",
            Visible = _scene.MinimizeToTray
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Показать", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Старт", null, (_, _) => StartPainting());
        menu.Items.Add("Стоп", null, (_, _) => StopPainting());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => { _reallyClosing = true; Close(); });

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    static System.Drawing.Icon TrayIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            return s == null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(s);
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    void RefreshUi()
    {
        PollDevices();

        if (_painter.IsRunning) Say(_painter.Status);

        if (_statValues.Length == StatRows.Length)
        {
            _statValues[0].Text = _painter.SourceInfo;
            _statValues[1].Text = _painter.Status;
            _statValues[2].Text = $"принято {_painter.FramesReceived}, отрисовано {_painter.FramesPainted}";
            _statValues[3].Text = $"{_painter.Fps:F1} в секунду";
            _statValues[4].Text = $"{_painter.LastFrameAgeMs} мс";
            _statValues[5].Text = _painter.LedCount.ToString();
            _statValues[6].Text = _hub.Status;
        }

        FollowBusScreen();

        if (_tray != null) _tray.Visible = _scene.MinimizeToTray;
    }

    /// <summary>
    /// Keeps the monitor rectangle matching the screen Rimlight is sending.
    ///
    /// With frames coming over the bus the choice of screen belongs to Rimlight, so the
    /// only honest thing to do is follow it. The size is measured, not chosen, so it is
    /// written onto the saved copy as well - the same treatment window geometry gets, and
    /// for the same reason: a pending-changes bar for something nobody edited is noise.
    /// </summary>
    void FollowBusScreen()
    {
        if (_scene.CaptureSource != CaptureSource.FromRimlight) return;

        string name = _painter.BusMonitorDeviceName;
        if (string.IsNullOrEmpty(name) || name == _adoptedBusScreen) return;

        var monitor = Native.EnumerateMonitors().FirstOrDefault(m => m.DeviceName == name);
        if (monitor == null) return;

        _adoptedBusScreen = name;

        var (w, h) = DisplaySize.Rect(monitor, _scene.Monitor.Width);
        if (Math.Abs(w - _scene.Monitor.Width) < 0.5 && Math.Abs(h - _scene.Monitor.Height) < 0.5) return;

        _scene.Monitor.Width = _saved.Monitor.Width = w;
        _scene.Monitor.Height = _saved.Monitor.Height = h;

        _painter.Invalidate();
        _view.InvalidateVisual();
        UpdateDirtyBar();
        Say($"Экран из Rimlight: {monitor.DisplayName}, {w:F0} × {h:F0} мм");
    }

    /// <summary>Which bus screen has already been taken, so it is measured once.</summary>
    string _adoptedBusScreen = "";

    /// <summary>
    /// Keeps the controller list current: connects when there is no connection, and keeps
    /// re-reading the list until it stops changing.
    ///
    /// Reconnecting is left to the tick rather than to the user: after a launch the port
    /// appears only once detection is finished, and the server dies by itself often enough
    /// that waiting for a button press is not reasonable. Connect() throttles its own
    /// retries. Re-reading is deliberately not a reconnect - remaking the connection is
    /// what crashes this server.
    /// </summary>
    void PollDevices()
    {
        if (_recovering) return;

        long now = Environment.TickCount64;

        if (!_hub.IsConnected)
        {
            if (_hub.Connect())
            {
                _settlePolls = SettlePolls;
                Say(_hub.Status);
                BuildFixturePanel();
                return;
            }

            if (_serverStartedTicks > 0 && now - _serverStartedTicks < OpenRgbLauncher.TypicalStartupMs)
            {
                Say("OpenRGB запускается, идёт поиск устройств.");
                return;
            }

            // The server dies on its own often enough that waiting for someone to notice
            // is not a plan: if it is gone and we are allowed to start it, start it.
            if (_scene.AutoStartOpenRgb && !OpenRgbLauncher.IsRunning())
            {
                Say("OpenRGB не отвечает, запускаю заново.");
                EnsureServer();
            }
            return;
        }

        // the server announces changes of its own accord; this is free when nothing moved
        if (_hub.RefreshIfStale())
        {
            _settlePolls = SettlePolls;
            BuildFixturePanel();
            _painter.Invalidate();
        }

        if (_settlePolls <= 0 || now - _lastListPoll < 2500) return;
        _lastListPoll = now;

        int before = _hub.Devices.Length;
        if (!_hub.TryRefresh()) return;

        if (_hub.Devices.Length != before)
        {
            // still filling up - start the count again rather than settle on a partial list
            _settlePolls = SettlePolls;
            Say(_hub.Status);
            BuildFixturePanel();
            _painter.Invalidate();
        }
        else if (_hub.Devices.Length == 0)
        {
            Say("OpenRGB подключён, устройств пока нет: идёт поиск.");
        }
        else
        {
            _settlePolls--;
        }
    }

    void Say(string text) => _status.Text = text;

    // ---- запуск -----------------------------------------------------------

    void ConnectHub()
    {
        if (_recovering) { Say("Идёт восстановление связи."); return; }

        EnsureServer();
        _hub.Connect(force: true);
        _settlePolls = SettlePolls;
        Say(_hub.Status);
        BuildFixturePanel();
    }

    /// <summary>
    /// Starts the server if it is not up. Does not wait for it: detection takes several
    /// seconds and the port only opens afterwards, so the reconnect in the UI tick picks it
    /// up when it is genuinely ready rather than freezing the window meanwhile.
    /// </summary>
    void EnsureServer()
    {
        if (!_scene.AutoStartOpenRgb || OpenRgbLauncher.IsRunning()) return;

        string path = string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? "" : _scene.OpenRgbPath;
        Say(OpenRgbLauncher.Launch(path.Length == 0 ? null : path, _scene.OpenRgbAsAdmin));
        _serverStartedTicks = Environment.TickCount64;
    }

    void StartPainting()
    {
        EnsureServer();

        // Starting does not wait for the server. The paint loop connects on its own and
        // keeps retrying, while the port opens only seconds after the server is launched -
        // so refusing here is what left a case dark after a launch with "start painting"
        // ticked, until someone pressed the button by hand.
        //
        // Deliberately not a forced reconnect either: OpenRGB dies on the client
        // disconnect - all three crashes in the log end on "Closing server connection" -
        // so an existing connection is worth far more than a fresh one.
        _hub.Connect();

        _painter.UseScene(_scene);
        _painter.Start();
        _paintingWanted = true;

        Say(_hub.IsReady ? "Раскраска запущена." : "Раскраска запущена, жду OpenRGB.");
    }

    /// <summary>
    /// Waits until the server both answers and has found something.
    ///
    /// A refused connection is harmless - unlike a dropped one, which is what kills this
    /// server - so retrying costs nothing. Once connected, the list is re-read rather than
    /// the connection remade, because detection finishes after the port opens and an empty
    /// list at that moment means "not yet", not "nothing here".
    /// </summary>
    bool WaitForDevices(int attempts = 90)
    {
        int last = -1, stable = 0;

        for (int i = 0; i < attempts; i++)
        {
            if (!_hub.IsConnected) _hub.Connect(force: true);
            else _hub.Refresh();

            int count = _hub.Devices.Length;

            // Not "any device", but "the same devices twice running": detection hands the
            // list over in pieces, and returning at the first one binds the layout to
            // whatever happened to be found first.
            stable = count > 0 && count == last ? stable + 1 : 0;
            last = count;

            if (stable >= 2) return true;
            System.Threading.Thread.Sleep(500);
        }

        return _hub.IsReady;
    }

    /// <summary>The same recovery, on demand - useful when sleep is not the cause.</summary>
    void RestartServerNow()
    {
        _recovering = true;
        _painter.Pause("перезапуск OpenRGB");
        Say("Перезапуск OpenRGB.");

        System.Threading.Tasks.Task.Run(() =>
        {
            string what = "";
            bool back = false;

            try
            {
                _hub.Dispose();
                what = OpenRgbLauncher.Restart(
                    string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath,
                    _scene.OpenRgbAsAdmin);

                back = WaitForDevices();
            }
            finally
            {
                _recovering = false;
            }

            Dispatcher.Invoke(() =>
            {
                Say(back ? $"{what}; подключились заново" : $"{what}; сервер не отвечает");
                BuildFixturePanel();

                _painter.Resume(0);
                if (_paintingWanted && !_painter.IsRunning) _painter.Start();
            });
        });
    }

    void StopPainting()
    {
        _paintingWanted = false;
        StopTest();
        _painter.Stop();
        Say("Раскраска остановлена, подсветка погашена.");
    }

    // ---- геометрия окна ---------------------------------------------------

    void RestoreWindowGeometry()
    {
        Width = Math.Max(1100, _scene.WindowWidth);
        Height = Math.Max(700, _scene.WindowHeight);
        MinWidth = 1100;
        MinHeight = 700;

        if (_scene.WindowLeft is double left && _scene.WindowTop is double top)
        {
            // only honour a saved position that still lands on an attached monitor
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (left > -Width && left < vw && top > -Height && top < vh)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_scene.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveWindowGeometry()
    {
        _scene.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds holds the pre-maximise rectangle; ActualWidth would save the
        // maximised size and the window would never come back to its normal shape
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (r.Width > 100 && r.Height > 100)
        {
            _scene.WindowWidth = r.Width;
            _scene.WindowHeight = r.Height;
            _scene.WindowLeft = r.Left;
            _scene.WindowTop = r.Top;
        }
    }
}
