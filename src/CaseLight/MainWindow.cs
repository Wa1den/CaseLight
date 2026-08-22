using System;
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

    TabControl _tabs = null!;
    Border _dirtyBar = null!;
    Border _fixtureOverlay = null!;
    StackPanel _fixturePanel = null!;
    ListBox _fixtureList = null!;
    TextBlock _status = null!;
    TextBlock _captureStats = null!;
    Button _testButton = null!;

    bool _rebuildingUi;
    bool _syncingList;

    /// <summary>When we last launched the server, so the wait can be reported honestly.</summary>
    long _serverStartedTicks;

    /// <summary>Guards against running the wake recovery twice for one wake.</summary>
    bool _wokeUp;

    /// <summary>Throttles re-reading the list while the server is still detecting.</summary>
    long _lastEmptyRefresh;

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
        _view.FixtureChanged += (_, _) => { _painter.Invalidate(); BuildFixturePanel(); };
        _view.TestMoved += (_, _) => PushTestPatch();

        HookPower();

        Loaded += (_, _) =>
        {
            _power.Attach(this);
            SetupTray();

            EnsureServer();
            ConnectHub();
            RebuildTabs();
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

        Closing += (_, _) =>
        {
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- слева: вкладки и полоса применения
        var left = new Grid { Margin = new Thickness(10, 10, 5, 10) };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _tabs = new TabControl();

        // The fixture panel belongs to one tab only; leaving it hanging over the canvas
        // while looking at, say, power settings is just clutter.
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == _tabs) HideFixturePanel();
        };

        Grid.SetRow(_tabs, 0);
        left.Children.Add(_tabs);

        _dirtyBar = BuildDirtyBar();
        Grid.SetRow(_dirtyBar, 1);
        left.Children.Add(_dirtyBar);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ---- справа: холст и панель фигуры поверх него
        var right = new Grid { Margin = new Thickness(5, 10, 10, 10) };
        right.Children.Add(_view);

        _fixturePanel = new StackPanel();
        _fixtureOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 34, 37, 44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(70, 76, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Width = 340,
            Margin = new Thickness(0, 10, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { Content = _fixturePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        right.Children.Add(_fixtureOverlay);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        // ---- низ: действия и статус
        var bottom = new StackPanel { Margin = new Thickness(10, 0, 10, 10), Orientation = Orientation.Horizontal };
        bottom.Children.Add(Ui.Btn("Старт", StartPainting));
        bottom.Children.Add(Ui.Btn("Стоп", StopPainting));
        bottom.Children.Add(Ui.Btn("Переподключиться", ConnectHub));
        bottom.Children.Add(Ui.Btn("Центрировать холст", () => _view.FitToContent()));

        _status = new TextBlock
        {
            Foreground = Brushes.Silver,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        bottom.Children.Add(_status);

        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 2);
        grid.Children.Add(bottom);

        return grid;
    }

    Border BuildDirtyBar()
    {
        var apply = Ui.Btn("Применить", ApplyChanges);
        var cancel = Ui.Btn("Отмена", CancelChanges);

        var dock = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(apply, Dock.Right);
        dock.Children.Add(cancel);
        dock.Children.Add(apply);
        dock.Children.Add(new TextBlock
        {
            Text = "Есть изменения, которые ещё не сохранены",
            Foreground = Ui.Warn,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = Ui.Panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = dock
        };
    }

    // ---- вкладки ----------------------------------------------------------

    void AddTab(string title, Action<StackPanel> build)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        build(panel);

        _tabs.Items.Add(new TabItem
        {
            Header = title,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        });
    }

    /// <summary>Rebuilt wholesale after Cancel or import, since every field may have moved.</summary>
    void RebuildTabs()
    {
        _rebuildingUi = true;

        int selected = _tabs.SelectedIndex;
        _tabs.Items.Clear();

        BuildGeneralTab();
        BuildDevicesTab();
        BuildCaptureTab();
        BuildColorsTab();
        BuildPowerTab();
        BuildAboutTab();

        if (selected >= 0 && selected < _tabs.Items.Count) _tabs.SelectedIndex = selected;

        _rebuildingUi = false;
    }

    void BuildGeneralTab() => AddTab("Основное", panel =>
    {
        panel.Children.Add(Ui.Header("Окно"));
        panel.Children.Add(Ui.Check("Сворачивать в трей", _scene.MinimizeToTray, v => { _scene.MinimizeToTray = v; Touch(); }));
        panel.Children.Add(Ui.Check("Запускать свёрнутым", _scene.StartMinimized, v => { _scene.StartMinimized = v; Touch(); }));

        panel.Children.Add(Ui.Header("Запуск"));
        panel.Children.Add(Ui.Check("Запускать вместе с Windows", Autostart.IsEnabled(), v => Say(Autostart.Set(v))));
        panel.Children.Add(Ui.Check("Сразу начинать раскраску", _scene.StartPaintingOnLaunch, v => { _scene.StartPaintingOnLaunch = v; Touch(); }));
        panel.Children.Add(Ui.Note("Подсветкой распоряжается OpenRGB, и ему нужны права администратора. " +
                                   "Автозапуск CaseLight поможет только если и OpenRGB стартует сам."));

        panel.Children.Add(Ui.Header("Сервер OpenRGB"));
        panel.Children.Add(Ui.Check("Запускать OpenRGB, если он не запущен", _scene.AutoStartOpenRgb, v => { _scene.AutoStartOpenRgb = v; Touch(); }));
        panel.Children.Add(Ui.Check("Запускать от администратора", _scene.OpenRgbAsAdmin, v => { _scene.OpenRgbAsAdmin = v; Touch(); }));
        panel.Children.Add(Ui.Note("Права нужны только ради оперативной памяти на шине SMBus. " +
                                   "Плата и видеокарта доступны и без них, зато не будет запроса UAC при каждом входе."));

        var pathBox = Ui.Text("Путь к OpenRGB.exe (пусто — найти самому)", _scene.OpenRgbPath, v => { _scene.OpenRgbPath = v; Touch(); });
        panel.Children.Add(pathBox);
        panel.Children.Add(Ui.Row(Ui.Btn("Найти", () =>
        {
            string? found = OpenRgbLauncher.FindExe();
            Say(found == null ? "OpenRGB.exe не нашёлся — укажи путь вручную" : "нашёл: " + found);
        }), Ui.Btn("Запустить сейчас", () => Say(OpenRgbLauncher.Launch(
            string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath, _scene.OpenRgbAsAdmin)))));

        panel.Children.Add(Ui.Header("Настройки"));
        panel.Children.Add(Ui.Row(Ui.Btn("Экспорт…", ExportSettings), Ui.Btn("Импорт…", ImportSettings)));
        panel.Children.Add(Ui.Note("Один файл со всем: раскладка, монитор, цвета, захват, питание."));

        panel.Children.Add(Ui.Header("Журнал"));
        panel.Children.Add(Ui.Check("Вести журнал", _scene.WriteLog, v =>
        {
            _scene.WriteLog = v;
            ProbeLog.Configure(Scene.LogPath, v);
            Touch();
        }));
        panel.Children.Add(Ui.Note(Scene.LogPath));
    });

    void BuildDevicesTab() => AddTab("Устройства", panel =>
    {
        panel.Children.Add(Ui.Header("Фигуры"));

        _fixtureList = new ListBox
        {
            Background = Ui.Panel,
            Foreground = Ui.Fg,
            BorderThickness = new Thickness(0),
            Height = 260
        };
        _fixtureList.SelectionChanged += (_, _) =>
        {
            if (_syncingList) return;
            _view.Select((_fixtureList.SelectedItem as FixtureItem)?.Fixture);
        };
        panel.Children.Add(_fixtureList);

        panel.Children.Add(Ui.Row(
            Ui.Btn("Добавить", AddFixture),
            Ui.Btn("Копия", DuplicateFixture),
            Ui.Btn("Удалить", RemoveFixture)));

        panel.Children.Add(Ui.Note("Выбери фигуру — её параметры откроются поверх холста."));

        panel.Children.Add(Ui.Header("Монитор"));
        panel.Children.Add(Ui.Note("Размер видимой картинки. От него считается, какой участок экрана видит каждый диод."));
        panel.Children.Add(Ui.Num("Ширина, мм", _scene.Monitor.Width, v => { _scene.Monitor.Width = Math.Max(10, v); Touch(); }));
        panel.Children.Add(Ui.Num("Высота, мм", _scene.Monitor.Height, v => { _scene.Monitor.Height = Math.Max(10, v); Touch(); }));

        SyncFixtureList();
    });

    void BuildCaptureTab() => AddTab("Захват", panel =>
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
            RebuildTabs();
            Touch();
        };
        panel.Children.Add(Ui.Labelled("Метод", box));

        if (_scene.CaptureSource == CaptureSource.FromRimlight)
        {
            panel.Children.Add(Ui.Note("Кадры приходят от Rimlight через разделяемую память — второго захвата экрана тогда нет вовсе. " +
                                       "Не забудь включить там «Отдавать снимки экрана в модуль подсветки»."));
        }
        else
        {
            panel.Children.Add(Ui.Note("Свой захват не зависит от Rimlight, но если тот работает одновременно, экран будет " +
                                       "сниматься дважды — это лишняя нагрузка на видеокарту."));

            var monitorBox = new ComboBox { Margin = new Thickness(0, 2, 0, 0) };
            var monitors = Native.EnumerateMonitors();

            monitorBox.Items.Add("Основной экран");
            foreach (var m in monitors)
                monitorBox.Items.Add($"{m.DisplayName} — {m.Width}×{m.Height}");

            int index = monitors.FindIndex(m => m.DeviceName == _scene.MonitorDeviceName);
            monitorBox.SelectedIndex = index >= 0 ? index + 1 : 0;

            monitorBox.SelectionChanged += (_, _) =>
            {
                int i = monitorBox.SelectedIndex;
                _scene.MonitorDeviceName = i <= 0 ? "" : monitors[i - 1].DeviceName;
                Touch();
            };
            panel.Children.Add(Ui.Labelled("Экран", monitorBox));

            panel.Children.Add(Ui.Note("DDA быстрее всех, но её присутствие иногда заставляет Windows рисовать курсор через " +
                                       "композицию — курсор начинает мерцать. WGC мягче, GDI работает всегда и всюду. " +
                                       "«Автоматически» держит DDA и WGC вместе, а GDI закрывает провалы."));
        }

        panel.Children.Add(Ui.Slide("Кадров в секунду", _scene.MaxFps, 1, 120, 1, v => { _scene.MaxFps = (int)v; Touch(); }));
        panel.Children.Add(Ui.Note("Верхний предел для быстрых устройств. Медленным можно задать свой делитель в параметрах фигуры."));

        panel.Children.Add(Ui.Header("Статистика"));
        _captureStats = Ui.Mono();
        panel.Children.Add(_captureStats);
    });

    void BuildPowerTab() => AddTab("Питание", panel =>
    {
        panel.Children.Add(Ui.Header("Гасить подсветку"));
        panel.Children.Add(Ui.Check("при выходе из программы", _scene.OffOnExit, v => { _scene.OffOnExit = v; Touch(); }));
        panel.Children.Add(Ui.Check("когда экран выключен", _scene.OffOnDisplayOff, v => { _scene.OffOnDisplayOff = v; Touch(); }));
        panel.Children.Add(Ui.Check("при блокировке сессии", _scene.OffOnLock, v => { _scene.OffOnLock = v; Touch(); }));
        panel.Children.Add(Ui.Check("при уходе в сон", _scene.OffOnSuspend, v => { _scene.OffOnSuspend = v; Touch(); }));

        panel.Children.Add(Ui.Header("После пробуждения"));

        var wakeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        wakeBox.Items.Add("Ничего не делать");
        wakeBox.Items.Add("Попросить OpenRGB пересканировать устройства");
        wakeBox.Items.Add("Перезапустить OpenRGB");
        wakeBox.SelectedIndex = (int)_scene.WakeRecovery;
        wakeBox.SelectionChanged += (_, _) =>
        {
            if (wakeBox.SelectedIndex < 0) return;
            _scene.WakeRecovery = (WakeRecovery)wakeBox.SelectedIndex;
            Touch();
        };
        panel.Children.Add(Ui.Labelled("Что делать", wakeBox));
        panel.Children.Add(Ui.Note("Во сне контроллеры переподключаются заново, а работавший сервер продолжает писать в " +
                                   "устаревшие хендлы: он рапортует успех, а корпус светится так же, как при загрузке. " +
                                   "Перезапуск грубее, но именно он надёжно возвращает управление. Пересканирование мягче, " +
                                   "однако способно уронить сервер."));

        panel.Children.Add(Ui.Row(Ui.Btn("Перезапустить OpenRGB сейчас", RestartServerNow)));

        panel.Children.Add(Ui.Slide("Пауза перед возвратом", _scene.ResumeDelayMs / 1000.0, 0, 30, 1,
                                    v => { _scene.ResumeDelayMs = (int)(v * 1000); Touch(); }, " с"));
        panel.Children.Add(Ui.Note("Во сне устройства переподключаются заново, и OpenRGB какое-то время держит устаревшие хендлы — " +
                                   "в журнале Windows он падал через 41 секунду после пробуждения. Пауза нужна, чтобы не мы оказались " +
                                   "теми, кто дёрнул шину в этот момент."));
    });

    void BuildAboutTab() => AddTab("О программе", panel =>
    {
        panel.Children.Add(Ui.Header("CaseLight " + AppVersion));

        panel.Children.Add(Ui.Note("Подсветка корпуса, повторяющая то, что происходит на экране. " +
                                   "Каждое светящееся место описывается там, где оно физически стоит, " +
                                   "и берёт цвет с ближайшего к нему участка картинки — системник рядом " +
                                   "с монитором читается как его продолжение."));

        panel.Children.Add(Ui.Note("Управление железом идёт через OpenRGB: материнская плата, ленты и " +
                                   "вентиляторы на её разъёмах, видеокарта, оперативная память. " +
                                   "Программа сама поднимает сервер, если он не запущен, и возвращает " +
                                   "подсветку к жизни после сна."));

        panel.Children.Add(Ui.Note("Кадры можно брать двумя способами: захватывать экран самостоятельно " +
                                   "или получать от Rimlight, подсветки монитора. Rimlight при этом " +
                                   "не обязателен — он нужен только тем, у кого есть лента за монитором " +
                                   "и отдельный контроллер к ней."));

        panel.Children.Add(Ui.Link("Репозиторий:", "https://github.com/Wa1den/CaseLight"));
        panel.Children.Add(Ui.Link("Rimlight, подсветка монитора:", "https://github.com/Wa1den/Rimlight"));
        panel.Children.Add(Ui.Link("OpenRGB:", "https://openrgb.org"));
    });

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
        RebuildTabs();
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
        catch (Exception ex) { Say("Не удалось выгрузить: " + ex.Message); }
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
            RebuildTabs();
            SyncFixtureList();
            _painter.Invalidate();
            _view.FitToContent();
            UpdateDirtyBar();

            Say("Импортировано. Проверь раскладку и нажми «Применить», если всё верно.");
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

            _painter.Resume(_scene.ResumeDelayMs);
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
        var mode = _scene.WakeRecovery;

        if (mode == WakeRecovery.Nothing)
        {
            _painter.Resume(_scene.ResumeDelayMs);
            return;
        }

        bool wasPainting = _painter.IsRunning;
        _recovering = true;

        // Stop writing before touching the server: a rescan with an active client is what
        // crashed it by hand, and a restart would be writing into a dying process.
        _painter.Pause("восстановление после сна");
        Say("Пробуждение: привожу OpenRGB в чувство…");

        System.Threading.Tasks.Task.Run(() =>
        {
            // let the USB stack finish re-enumerating before anything is asked of it
            System.Threading.Thread.Sleep(Math.Max(2000, _scene.ResumeDelayMs));

            string what;
            if (mode == WakeRecovery.Rescan)
            {
                what = RgbHub.RequestRescan();
                System.Threading.Thread.Sleep(6000);   // поиск устройств занимает секунды
            }
            else
            {
                _hub.Dispose();                        // клиента всё равно унесёт вместе с сервером
                what = OpenRgbLauncher.Restart(
                    string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath,
                    _scene.OpenRgbAsAdmin);
            }

            bool back = WaitForDevices();

            Dispatcher.Invoke(() =>
            {
                Say(back
                    ? $"{what}; подсветка восстановлена"
                    : $"{what}; сервер не отвечает — нажми «Переподключиться»");

                BuildFixturePanel();

                _painter.Resume(0);
                if (wasPainting && !_painter.IsRunning) _painter.Start();
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
        menu.Items.Add("Выход", null, (_, _) => Close());

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
        // Reconnect on its own: after a launch the port appears only once detection is
        // finished, and the server also dies by itself often enough that waiting for the
        // user to press a button is not reasonable. Connect() throttles its own retries.
        if (!_hub.IsReady && !_recovering)
        {
            if (!_hub.IsConnected)
            {
                if (_hub.Connect()) { Say(_hub.Status); BuildFixturePanel(); }
                else if (_serverStartedTicks > 0 && Environment.TickCount64 - _serverStartedTicks < OpenRgbLauncher.TypicalStartupMs)
                    Say("OpenRGB запускается и ищет устройства…");
            }
            else if (Environment.TickCount64 - _lastEmptyRefresh > 2000)
            {
                // Connected but empty: detection is still running on the other side. Ask
                // again instead of reconnecting - remaking the connection is what crashes it.
                _lastEmptyRefresh = Environment.TickCount64;
                _hub.Refresh();

                if (_hub.IsReady) { Say(_hub.Status); BuildFixturePanel(); }
                else Say("OpenRGB подключен, но устройств пока нет — ищет…");
            }
        }

        if (_painter.IsRunning) Say(_painter.Status);

        if (_captureStats != null)
        {
            _captureStats.Text =
                $"источник:   {_painter.SourceInfo}\n" +
                $"состояние:  {_painter.Status}\n" +
                $"кадров:     принято {_painter.FramesReceived}, отрисовано {_painter.FramesPainted}\n" +
                $"частота:    {_painter.Fps:F1} к/с\n" +
                $"возраст:    {_painter.LastFrameAgeMs} мс\n" +
                $"диодов:     {_painter.LedCount}\n" +
                $"OpenRGB:    {_hub.Status}";
        }

        if (_tray != null) _tray.Visible = _scene.MinimizeToTray;
    }

    void Say(string text) => _status.Text = text;

    // ---- запуск -----------------------------------------------------------

    void ConnectHub()
    {
        if (_recovering) { Say("Идёт восстановление связи, подожди…"); return; }

        EnsureServer();
        _hub.Connect(force: true);
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

        // Deliberately not a forced reconnect: OpenRGB dies on the client disconnect - all
        // three crashes in the log end on "Closing server connection" - so an existing
        // connection is worth far more than a fresh one.
        if (!_hub.Connect()) { Say(_hub.Status); return; }

        _painter.UseScene(_scene);
        _painter.Start();
        Say("Раскраска запущена.");
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
        for (int i = 0; i < attempts; i++)
        {
            if (!_hub.IsConnected) _hub.Connect(force: true);
            else _hub.Refresh();

            if (_hub.IsReady) return true;
            System.Threading.Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>The same recovery, on demand - useful when sleep is not the cause.</summary>
    void RestartServerNow()
    {
        bool wasPainting = _painter.IsRunning;
        _recovering = true;
        _painter.Pause("перезапуск OpenRGB");
        Say("Перезапускаю OpenRGB…");

        System.Threading.Tasks.Task.Run(() =>
        {
            _hub.Dispose();
            string what = OpenRgbLauncher.Restart(
                string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath,
                _scene.OpenRgbAsAdmin);

            bool back = WaitForDevices();

            Dispatcher.Invoke(() =>
            {
                Say(back ? $"{what}; подключились заново" : $"{what}; сервер не отвечает");
                BuildFixturePanel();

                _painter.Resume(0);
                if (wasPainting && !_painter.IsRunning) _painter.Start();
                _recovering = false;
            });
        });
    }

    void StopPainting()
    {
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
