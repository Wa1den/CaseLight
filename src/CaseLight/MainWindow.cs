using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaseLight.Core.Capture;
using CaseLight.Core.Power;
using CaseLight.Model;
using CaseLight.Render;
using CaseLight.Rgb;
using CaseLight.View;

using CaseLight.Core.Text;

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

    /// <summary>
    /// Drives the picture on the canvas, and only while it is being shown.
    ///
    /// Separate from the interface tick because the two want different rates: status text
    /// is fine twice a second, a picture at that rate looks broken.
    /// </summary>
    readonly DispatcherTimer _screen = new() { Interval = TimeSpan.FromMilliseconds(120) };

    byte[] _screenBuffer = Array.Empty<byte>();
    long _screenVersion;

    /// <summary>Takes the sampling circle off the canvas once the value has settled.</summary>
    readonly DispatcherTimer _sampleHint = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    Scene _scene = Scene.Load();
    Scene _saved = null!;
    CasePainter _painter = null!;

    System.Windows.Forms.NotifyIcon? _tray;

    ListBox _nav = null!;
    ContentControl _pageHost = null!;
    readonly List<UIElement> _pages = new();

    ColumnDefinition _canvasColumn = null!;
    Grid _canvasHost = null!;
    DockPanel _rail = null!;
    DockPanel _bottomBar = null!;
    CheckBox _canvasToggle = null!;
    Button _startButton = null!;
    Button _stopButton = null!;
    Button _applyButton = null!;
    Button _cancelButton = null!;
    TextBlock _dirtyText = null!;
    Button _fitButton = null!;
    UIElement _screenToggle = null!;

    /// <summary>Window width with the canvas open, to come back to when it is shown again.</summary>
    double _wideWidth;
    bool? _canvasShown;

    /// <summary>Narrower than this the canvas has no room worth the name.</summary>
    const double WideMinWidth = 1100;

    /// <summary>Width of the settings page, the same with the canvas and without it.</summary>
    const double PageWidth = 440;
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
        ProbeLog.Configure(Scene.LogPath, _scene.WriteLog);

        // Раньше всего остального: дальше собираются подписи, а они уже переведённые.
        Loc.Configure(System.IO.Path.Combine(Scene.Folder, "lang"));
        Loc.Load(_scene.Language);

        Title = Loc.T("app.title");

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
            ApplyScreenPreview();
            ApplyCanvasVisibility();
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

        _screen.Tick += (_, _) => UpdateScreen();

        _sampleHint.Tick += (_, _) =>
        {
            _sampleHint.Stop();
            _view.ShowSampleArea = false;
            _view.InvalidateVisual();
        };

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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PageWidth) });

        _canvasColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        grid.ColumnDefinitions.Add(_canvasColumn);

        // ---- слева: столбец разделов шириной по самой длинной подписи
        _nav = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top
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

        // The canvas switch lives under the sections rather than in them: it is about the
        // shape of the window, not about any one page.
        _canvasToggle = (CheckBox)Ui.Check(Loc.T("nav.canvas"), _scene.ShowCanvas,
            v => { _scene.ShowCanvas = v; ApplyCanvasVisibility(); Touch(); });

        _canvasToggle.Margin = new Thickness(12, 12, 0, 0);
        DockPanel.SetDock(_canvasToggle, Dock.Bottom);

        _rail = new DockPanel { Margin = new Thickness(6, 12, 6, 12) };
        _rail.Children.Add(_canvasToggle);
        _rail.Children.Add(_nav);

        Grid.SetColumn(_rail, 0);
        grid.Children.Add(_rail);

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
        _canvasHost = right;
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
        var bottom = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
        _bottomBar = bottom;

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _startButton = Ui.Btn(Loc.T("bar.start"), StartPainting);
        _stopButton = Ui.Btn(Loc.T("bar.stop"), StopPainting);
        actions.Children.Add(_startButton);
        actions.Children.Add(_stopButton);

        _fitButton = Ui.Btn(Loc.T("bar.fit"), () => _view.FitToContent());
        actions.Children.Add(_fitButton);
        DockPanel.SetDock(actions, Dock.Left);
        bottom.Children.Add(actions);

        // The bar is exactly as tall as its buttons, whatever else stands in it. The screen
        // checkbox measures taller than they do, so with the canvas shown it set the height
        // of the whole bar and the buttons rode along its top edge - and hiding the canvas
        // took the checkbox away and dropped them by those few pixels.
        actions.SizeChanged += (_, _) => bottom.Height = actions.ActualHeight;

        _screenToggle = BuildScreenToggle();
        bottom.Children.Add(_screenToggle);

        _status = new TextBlock
        {
            Foreground = Ui.FgDim,
            FontSize = Ui.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),

            // One line always: a message with a file path in it would otherwise wrap to
            // three and lift the whole bottom bar, and the bar moving about under the
            // buttons is worse than a tail behind an ellipsis. The full text is in the
            // tooltip, so nothing said here is out of reach.
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        bottom.Children.Add(_status);

        // The bar is kept out of the grid on purpose. Spanning it across the columns made
        // its own width a claim on them, and the first column is auto-sized: one status line
        // about a lost connection stretched that column until the settings page hung off the
        // right edge of the window. Docked here it takes what the window has, no more.
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(grid);

        return root;
    }

    /// <summary>
    /// The screen switch, built apart from the bar so a language change can put a fresh one
    /// in its place: its tooltip is inside the element, and there is nothing to reassign.
    /// </summary>
    UIElement BuildScreenToggle()
    {
        // Under the canvas rather than in the settings: it is a way of looking at the
        // layout, switched on and off while working on it, not something to set once.
        var toggle = Ui.Check(Loc.T("bar.screen"), _scene.ShowScreen,
            v => { _scene.ShowScreen = v; ApplyScreenPreview(); Touch(); },
            Loc.T("bar.screen.note"));

        if (toggle is FrameworkElement box) box.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(toggle, Dock.Right);
        return toggle;
    }

    Border BuildDirtyBar()
    {
        _applyButton = Ui.Btn(Loc.T("bar.apply"), ApplyChanges, accent: true);
        _cancelButton = Ui.Btn(Loc.T("bar.cancel"), CancelChanges);

        var apply = _applyButton;
        var cancel = _cancelButton;

        _dirtyText = new TextBlock
        {
            Text = Loc.T("bar.dirty"),
            Foreground = Ui.Warn,
            FontSize = Ui.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        var dock = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(apply, Dock.Right);
        dock.Children.Add(cancel);
        dock.Children.Add(apply);
        dock.Children.Add(_dirtyText);

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

        // The card already keeps its own padding, and the first heading adds its gap on top
        // of it - twice the space above the first group as between the rest.
        if (panel.Children.Count > 0 && panel.Children[0] is FrameworkElement first)
            first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);

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

        // the settings may have arrived from a cancel or an import, not from a checkbox
        ApplyScreenPreview();
        ApplyCanvasVisibility();
    }

    void BuildGeneralSection() => AddSection(Loc.T("tab.main"), "\uE713", panel =>
    {
        var langBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var code in Loc.Available) langBox.Items.Add(Loc.DisplayName(code));
        langBox.SelectedIndex = Math.Max(0, Array.IndexOf(Loc.Available, Loc.Language));
        langBox.SelectionChanged += (_, _) =>
        {
            if (_rebuildingUi) return;

            _scene.Language = Loc.Available[Math.Max(0, langBox.SelectedIndex)];
            Touch();
            ApplyLanguage();
        };
        panel.Children.Add(Ui.Labeled(Loc.T("main.language"), langBox, Loc.T("main.language.note")));

        panel.Children.Add(Ui.Header(Loc.T("main.window")));
        panel.Children.Add(Ui.Check(Loc.T("main.tray"), _scene.MinimizeToTray, v => { _scene.MinimizeToTray = v; Touch(); },
            Loc.T("main.tray.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.startmin"), _scene.StartMinimized, v => { _scene.StartMinimized = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("main.startup")));
        panel.Children.Add(Ui.Check(Loc.T("main.autostart"), Autostart.IsEnabled(), v => Say(Autostart.Set(v)),
            Loc.T("main.autostart.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.autopaint"), _scene.StartPaintingOnLaunch, v => { _scene.StartPaintingOnLaunch = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("main.server")));
        panel.Children.Add(Ui.Check(Loc.T("main.serverstart"), _scene.AutoStartOpenRgb, v => { _scene.AutoStartOpenRgb = v; Touch(); },
            Loc.T("main.serverstart.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.admin"), _scene.OpenRgbAsAdmin, SetRunAsAdmin,
            Loc.T("main.admin.note")));

        panel.Children.Add(Ui.Check(Loc.T("main.task"), OpenRgbTask.Exists(), SetLogonTask,
            Loc.T("main.task.note"),
            enabled: _scene.OpenRgbAsAdmin));

        // Shown, not stored: the setting stays empty so the search runs again if OpenRGB
        // ever moves, while the field says which file that search lands on today.
        string knownPath = string.IsNullOrWhiteSpace(_scene.OpenRgbPath)
            ? OpenRgbLauncher.FindExe() ?? ""
            : _scene.OpenRgbPath;

        panel.Children.Add(Ui.Text(Loc.T("main.path"), knownPath, v => { _scene.OpenRgbPath = v; Touch(); },
            Loc.T("main.path.note")));
        panel.Children.Add(Ui.Row(Ui.Btn(Loc.T("main.find"), () =>
        {
            string? found = OpenRgbLauncher.FindExe();
            Say(found == null ? Loc.P("OpenRGB.exe не найден, укажите путь вручную", "OpenRGB.exe not found, set the path by hand") : Loc.P("Найден: ", "Found: ") + found);
        }), Ui.Btn(Loc.T("main.launch"), () => Say(OpenRgbLauncher.Launch(
            string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath, _scene.OpenRgbAsAdmin))),
            Ui.Btn(Loc.T("main.reconnect"), ConnectHub)));

        panel.Children.Add(Ui.Header(Loc.T("main.settings"),
            Loc.T("main.settings.note")));
        panel.Children.Add(Ui.Row(Ui.Btn(Loc.T("main.export"), ExportSettings), Ui.Btn(Loc.T("main.import"), ImportSettings)));

        panel.Children.Add(Ui.Header(Loc.T("main.logs")));
        panel.Children.Add(Ui.Check(Loc.T("main.log"), _scene.WriteLog, v =>
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

        var header = Ui.Header(Loc.T("devices.fixtures"),
            Loc.T("devices.fixtures.note"));
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
            Ui.Btn(Loc.T("devices.add"), AddFixture),
            Ui.Btn(Loc.T("devices.copy"), DuplicateFixture),
            Ui.Btn(Loc.T("devices.remove"), RemoveFixture));
        buttons.Margin = new Thickness(0, 0, 0, 0);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        var showDisabled = Ui.Check(Loc.T("devices.showdisabled"), _scene.ShowDisabled,
            v => { _scene.ShowDisabled = v; Touch(); },
            Loc.T("devices.showdisabled.note"));
        Grid.SetRow(showDisabled, 3);
        grid.Children.Add(showDisabled);

        AddSection(Loc.T("tab.devices"), "\uE772", Ui.Card(grid));
        SyncFixtureList();
    }

    void BuildCaptureSection() => AddSection(Loc.T("tab.capture"), "\uE7F4", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("capture.source")));

        var box = new ComboBox { Margin = new Thickness(0, 2, 0, 0) };
        box.Items.Add(Loc.T("capture.fromrimlight"));
        box.Items.Add(Loc.T("capture.auto"));
        box.Items.Add(Loc.T("capture.dda"));
        box.Items.Add(Loc.T("capture.wgc"));
        box.Items.Add(Loc.T("capture.gdi"));
        box.SelectedIndex = (int)_scene.CaptureSource;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            _scene.CaptureSource = (CaptureSource)box.SelectedIndex;
            RebuildSections();
            Touch();
        };

        panel.Children.Add(Ui.Labeled(Loc.T("capture.method"), box,
            Loc.T("capture.method.note")));

        // ---- экран
        bool ourCapture = _scene.CaptureSource != CaptureSource.FromRimlight;
        var monitors = ScreenChoice.Monitors(fresh: true);

        var monitorBox = new ComboBox { Margin = new Thickness(0, 2, 0, 0), IsEnabled = ourCapture };
        foreach (var m in monitors) monitorBox.Items.Add(m.ToString());

        var chosen = ScreenChoice.Find(_scene.MonitorDeviceName, _scene.MonitorModel);
        monitorBox.SelectedIndex = Math.Max(0, monitors.FindIndex(m => m.DeviceName == chosen?.DeviceName));

        monitorBox.SelectionChanged += (_, _) =>
        {
            int i = monitorBox.SelectedIndex;
            if (i < 0 || i >= monitors.Count) return;

            _scene.MonitorDeviceName = monitors[i].DeviceName;
            _scene.MonitorModel = monitors[i].Model;
            AdoptScreen(monitors[i]);

            // the rectangle can change shape entirely - an ultrawide for a portrait screen -
            // so bring the whole layout back into view rather than leave it off the edge
            _view.FitToContent();

            RebuildSections();
            Touch();
        };

        panel.Children.Add(Ui.Labeled(Loc.T("capture.screen"), monitorBox,
            Loc.T("capture.screen.note")));

        panel.Children.Add(Ui.Note(string.Format(Loc.T("capture.rect"),
            _scene.Monitor.Width.ToString("F0"), _scene.Monitor.Height.ToString("F0"))));

        panel.Children.Add(Ui.Slider(Loc.T("capture.fps"), _scene.MaxFps, 1, 120, 1, v => { _scene.MaxFps = (int)v; Touch(); }, "",
            Loc.T("capture.fps.note")));

        panel.Children.Add(Ui.Slider(Loc.T("capture.radius"), _scene.SampleRadiusMm, 1, 100, 1,
            v => { _scene.SampleRadiusMm = Math.Max(1, v); ShowSampleArea(); Touch(); }, Loc.T("unit.mm"),
            Loc.T("capture.radius.note")));

        panel.Children.Add(Ui.Header(Loc.T("capture.stats")));
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

    /// <summary>
    /// Собирается заново на каждый вызов: подписи зависят от языка, а массив, посчитанный
    /// один раз при загрузке типа, оставался бы на языке, который стоял при запуске.
    /// </summary>
    static string[] StatRows() => new[]
        { Loc.T("stats.source"), Loc.T("stats.state"), Loc.T("stats.frames"), Loc.T("stats.rate"), Loc.T("stats.latency"), Loc.T("stats.leds"), "OpenRGB" };

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

        var rows = StatRows();
        _statValues = new TextBlock[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = rows[i],
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

    void BuildPowerSection() => AddSection(Loc.T("tab.power"), "\uE7E8", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("power.off")));
        panel.Children.Add(Ui.Check(Loc.T("power.off.exit"), _scene.OffOnExit, v => { _scene.OffOnExit = v; Touch(); }));
        panel.Children.Add(Ui.Check(Loc.T("power.off.display"), _scene.OffOnDisplayOff, v => { _scene.OffOnDisplayOff = v; Touch(); }));
        panel.Children.Add(Ui.Check(Loc.T("power.off.lock"), _scene.OffOnLock, v => { _scene.OffOnLock = v; Touch(); }));
        panel.Children.Add(Ui.Check(Loc.T("power.off.sleep"), _scene.OffOnSuspend, v => { _scene.OffOnSuspend = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("power.wake")));

        var wakeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        wakeBox.Items.Add(Loc.T("power.wake.nothing"));
        wakeBox.Items.Add(Loc.T("power.wake.restart"));
        wakeBox.SelectedIndex = Math.Max(0, Array.IndexOf(WakeModes, WakeMode));
        wakeBox.SelectionChanged += (_, _) =>
        {
            if (wakeBox.SelectedIndex < 0) return;
            _scene.WakeRecovery = WakeModes[wakeBox.SelectedIndex];
            Touch();
        };
        panel.Children.Add(Ui.Labeled(Loc.T("power.wake.what"), wakeBox,
            Loc.T("power.wake.note")));

        panel.Children.Add(Ui.Row(Ui.Btn(Loc.T("power.restartnow"), RestartServerNow)));

        panel.Children.Add(Ui.Slider(Loc.T("power.delay"), _scene.ResumeDelayMs / 1000.0, 0, 30, 1,
            v => { _scene.ResumeDelayMs = (int)(v * 1000); Touch(); }, Loc.T("unit.s"),
            Loc.T("power.delay.note")));
    });

    /// <summary>
    /// The recovery modes offered. Rescanning is not among them: the request kills the
    /// server on this hardware, and with the restart no longer costing a rights prompt it
    /// had nothing left to offer. Settings that still name it are read as a restart.
    /// </summary>
    static readonly WakeRecovery[] WakeModes = { WakeRecovery.Nothing, WakeRecovery.RestartServer };

    WakeRecovery WakeMode =>
        _scene.WakeRecovery == WakeRecovery.Rescan ? WakeRecovery.RestartServer : _scene.WakeRecovery;

    void BuildAboutSection() => AddSection(Loc.T("tab.about"), "\uE897", panel =>
    {
        panel.Children.Add(Ui.Header("CaseLight " + AppVersion));

        panel.Children.Add(Ui.Note(Loc.T("about.text")));

        panel.Children.Add(Ui.Note(Loc.T("about.text2")));

        panel.Children.Add(Ui.Note(Loc.T("about.text3")));

        panel.Children.Add(Ui.Link(Loc.T("about.repo"), "https://github.com/Wa1den/CaseLight"));
        panel.Children.Add(Ui.Link(Loc.T("about.rimlight"), "https://github.com/Wa1den/Rimlight"));
        panel.Children.Add(Ui.Link("OpenRGB:", "https://openrgb.org"));
    });

    /// <summary>
    /// The two ways of getting the server its rights, of which this is the first.
    ///
    /// Switching it off takes the second one with it: a logon task that starts the server
    /// elevated is precisely "run as administrator", so leaving it registered would keep
    /// doing the thing that was just switched off.
    /// </summary>
    void SetRunAsAdmin(bool enabled)
    {
        if (_rebuildingUi) return;

        _scene.OpenRgbAsAdmin = enabled;

        if (!enabled && OpenRgbTask.Exists()) Say(OpenRgbTask.Delete());

        Touch();
        RebuildSections();
    }

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
        Say(Loc.P("Настройки применены и сохранены.", "Settings applied and saved."));
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
        Say(Loc.P("Изменения отменены.", "Changes discarded."));
    }

    void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("main.exporttitle"),
            Filter = Loc.T("main.filter"),
            FileName = "caselight-settings.json"
        };

        if (dialog.ShowDialog() != true) return;

        try { _scene.Save(dialog.FileName); Say(Loc.P("Экспортировано: ", "Exported: ") + dialog.FileName); }
        catch (Exception ex) { Say(Loc.P("Не удалось сохранить: ", "Could not save: ") + ex.Message); }
    }

    void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("main.importtitle"),
            Filter = Loc.T("main.filter")
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

            Say(Loc.P("Импортировано. Проверьте раскладку и нажмите «Применить».", "Imported. Check the layout and press «Apply»."));
        }
        catch (Exception ex)
        {
            Say(Loc.P("Не удалось прочитать файл: ", "Could not read the file: ") + ex.Message);
        }
    }

    // ---- питание, трей, статус --------------------------------------------

    void HookPower()
    {
        _power.Changed += (_, state) =>
        {
            string? reason =
                state.Suspended && _scene.OffOnSuspend ? Loc.P("сон", "sleep") :
                state.Locked && _scene.OffOnLock ? Loc.P("блокировка", "locked") :
                state.DisplayOff && _scene.OffOnDisplayOff ? Loc.P("экран выключен", "display off") :
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
        _painter.Pause(Loc.P("восстановление после сна", "recovery after sleep"));
        Say(Loc.P("Пробуждение: восстановление связи с OpenRGB.", "Waking: restoring the connection to OpenRGB."), 6000);

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
                Say(string.Format(back
                    ? Loc.P("{0}; подсветка восстановлена", "{0}; the lighting is back")
                    : Loc.P("{0}; сервер не отвечает, нажмите «Переподключиться»",
                            "{0}; the server is not responding, press «Reconnect»"), what));

                BuildFixturePanel();

                _painter.Resume(0);
                if (_paintingWanted && !_painter.IsRunning) _painter.Start();
            });
        });
    }

    void SetupTray()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "CaseLight",
            Visible = _scene.MinimizeToTray
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.T("bar.start"), null, (_, _) => StartPainting());
        menu.Items.Add(Loc.T("bar.stop"), null, (_, _) => StopPainting());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => { _reallyClosing = true; Close(); });

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

        if (_painter.IsRunning) SayFromTick(_painter.Status);

        if (_statValues.Length == StatRows().Length)
        {
            _statValues[0].Text = _painter.SourceInfo;
            _statValues[1].Text = _painter.Status;
            _statValues[2].Text = string.Format(Loc.P("принято {0}, отрисовано {1}", "received {0}, painted {1}"),
                                                _painter.FramesReceived, _painter.FramesPainted);
            _statValues[3].Text = string.Format(Loc.P("{0} в секунду", "{0} per second"), _painter.Fps.ToString("F1"));
            _statValues[4].Text = string.Format(Loc.P("{0} мс", "{0} ms"), _painter.LastFrameAgeMs);
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

        var monitor = ScreenChoice.Monitors().FirstOrDefault(m => m.DeviceName == name)
                   ?? ScreenChoice.Monitors(fresh: true).FirstOrDefault(m => m.DeviceName == name);

        if (monitor == null) return;

        _adoptedBusScreen = name;

        var (w, h) = DisplaySize.Rect(monitor, _scene.Monitor.Width);
        if (Math.Abs(w - _scene.Monitor.Width) < 0.5 && Math.Abs(h - _scene.Monitor.Height) < 0.5) return;

        _scene.Monitor.Width = _saved.Monitor.Width = w;
        _scene.Monitor.Height = _saved.Monitor.Height = h;

        _painter.Invalidate();
        _view.InvalidateVisual();
        UpdateDirtyBar();
        Say(string.Format(Loc.P("Экран из Rimlight: {0}, {1} × {2} мм", "Screen from Rimlight: {0}, {1} × {2} mm"),
                          monitor.DisplayName, w.ToString("F0"), h.ToString("F0")));

        // the screen we are modelling has been settled by the bus, so remember which one
        _scene.MonitorDeviceName = _saved.MonitorDeviceName = monitor.DeviceName;
        _scene.MonitorModel = _saved.MonitorModel = monitor.Model;
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
                SayFromTick(Loc.P("OpenRGB запускается, идёт поиск устройств.", "OpenRGB is starting, looking for devices."), 1200);
                return;
            }

            // The server dies on its own often enough that waiting for someone to notice
            // is not a plan: if it is gone and we are allowed to start it, start it.
            if (_scene.AutoStartOpenRgb && !OpenRgbLauncher.IsRunning())
            {
                Say(Loc.P("OpenRGB не отвечает, идёт перезапуск.", "OpenRGB is not responding, restarting."), 6000);
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
            Say(Loc.P("OpenRGB подключён, устройств пока нет: идёт поиск.", "OpenRGB connected, no devices yet: still looking."));
        }
        else
        {
            _settlePolls--;
        }
    }

    /// <summary>
    /// Puts the sampling circle on the canvas and takes it away once the value stops moving.
    ///
    /// Tied to the value rather than to the mouse: the slider answers to the wheel and to
    /// the arrow keys as well, and a hint that appears for only one of the three ways of
    /// using it would look broken.
    /// </summary>
    void ShowSampleArea()
    {
        _view.SampleAreaMm = _scene.SampleRadiusMm;
        _view.ShowSampleArea = true;
        _view.InvalidateVisual();

        _sampleHint.Stop();
        _sampleHint.Start();
    }

    /// <summary>
    /// Shows or hides the canvas, and gives the window back the width it had.
    ///
    /// Without the canvas the width is fixed and the window is not allowed to follow its own
    /// content: the bottom bar spans all three columns, so anything too wide for them grows
    /// the first column, which is auto-sized - one long status line about a lost connection
    /// was enough to widen the whole window. A width of its own also leaves the height
    /// alone, which sizing to content did not.
    /// </summary>
    void ApplyCanvasVisibility()
    {
        bool show = _scene.ShowCanvas;
        if (_canvasShown == show) return;
        _canvasShown = show;

        _fitButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _screenToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            _canvasHost.Visibility = Visibility.Visible;
            _canvasColumn.Width = new GridLength(1, GridUnitType.Star);

            MaxWidth = double.PositiveInfinity;
            MinWidth = WideMinWidth;
            if (IsLoaded && WindowState == WindowState.Normal)
                Width = Math.Max(WideMinWidth, _wideWidth);
            return;
        }

        if (IsLoaded && WindowState == WindowState.Normal && ActualWidth >= WideMinWidth)
            _wideWidth = ActualWidth;

        double narrow = NarrowWidth();

        _canvasHost.Visibility = Visibility.Collapsed;
        _canvasColumn.Width = new GridLength(0);

        MinWidth = 0;
        Width = narrow;
        MinWidth = MaxWidth = narrow;
    }

    /// <summary>
    /// What is left of the window once the canvas is gone: the section rail, the settings
    /// page and the window frame. Added up rather than asked of the layout, because the
    /// point is a width that does not depend on what is written in the window.
    ///
    /// The rail is measured, not read: a rebuild of the sections changes its captions, and
    /// before the window is shown nothing has been laid out at all. DesiredSize covers its
    /// margins either way. The page's own right margin is left out - there is no canvas
    /// beside it to keep clear of, and the window frame leaves a gap there anyway.
    /// </summary>
    double NarrowWidth()
    {
        if (IsLoaded) UpdateLayout();
        else if (_rail.DesiredSize.Width <= 0)
            _rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Пока окно не показано, рамку измерить нечем, поэтому стартовое значение
        // приблизительное; в Loaded расчёт повторяется и заменяет его точным.
        double frame = Content is FrameworkElement root && root.ActualWidth > 0
            ? ActualWidth - root.ActualWidth
            : 16;

        return _rail.DesiredSize.Width + PageWidth + frame;
    }

    /// <summary>
    /// Puts the window into the chosen language.
    ///
    /// The pages are built in code, so they are simply built again. What lives longer than a
    /// page is relabelled by hand: the title, the buttons of the bottom bar, the unsaved
    /// changes bar and the tray menu. The two switches carry a tooltip inside them and are
    /// replaced whole rather than relabelled.
    /// </summary>
    void ApplyLanguage()
    {
        Loc.Load(_scene.Language);

        Title = Loc.T("app.title");
        _canvasToggle.Content = Loc.T("nav.canvas");
        _startButton.Content = Loc.T("bar.start");
        _stopButton.Content = Loc.T("bar.stop");
        _fitButton.Content = Loc.T("bar.fit");
        _applyButton.Content = Loc.T("bar.apply");
        _cancelButton.Content = Loc.T("bar.cancel");
        _dirtyText.Text = Loc.T("bar.dirty");

        int at = _bottomBar.Children.IndexOf(_screenToggle);
        _bottomBar.Children.RemoveAt(at);
        _screenToggle = BuildScreenToggle();
        _bottomBar.Children.Insert(at, _screenToggle);
        _screenToggle.Visibility = _scene.ShowCanvas ? Visibility.Visible : Visibility.Collapsed;

        SetupTray();
        RebuildSections();
        SyncFixtureList();
        BuildFixturePanel();

        // Ширина рейла меняется вместе с длиной подписей, а от неё считается узкое окно.
        if (!_scene.ShowCanvas)
        {
            _canvasShown = null;
            ApplyCanvasVisibility();
        }
    }

    /// <summary>Starts or stops showing the screen on the canvas, per the setting.</summary>
    void ApplyScreenPreview()
    {
        bool on = _scene.ShowScreen;
        _painter.PreviewWanted = on;

        if (on)
        {
            _screen.Start();
            return;
        }

        _screen.Stop();
        _view.Screen = null;
        _view.InvalidateVisual();
    }

    /// <summary>
    /// Puts the newest frame on the canvas.
    ///
    /// The frame is the reduced one the painting itself works from - a few hundred pixels
    /// across - so building an image out of it every tick is cheaper than it looks, and it
    /// is skipped entirely when nothing new has arrived.
    /// </summary>
    void UpdateScreen()
    {
        if (!_painter.IsRunning)
        {
            // nothing is being captured, and a frozen picture of what used to be on screen
            // would be read as the current one
            if (_view.Screen == null) return;

            _view.Screen = null;
            _view.InvalidateVisual();
            return;
        }

        if (!_painter.TryTakePreview(ref _screenBuffer, ref _screenVersion, out int w, out int h, out int stride))
            return;

        if (w <= 0 || h <= 0 || stride <= 0) return;

        var frame = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, _screenBuffer, stride);
        frame.Freeze();   // построен не в потоке отрисовки, поэтому только замороженным

        _view.Screen = frame;
        _view.InvalidateVisual();
    }

    /// <summary>
    /// Puts a line in the status bar, and optionally keeps it there for a while.
    ///
    /// A hold is needed because the timer has something to say twice a second, and a dead
    /// server makes that "связь потеряна". The line about the server being restarted was
    /// replaced half a second later by the next tick, so the restart looked like nothing at
    /// all happened - which is how it was reported.
    /// </summary>
    void Say(string text, int holdMs = 0)
    {
        _status.Text = text;
        _status.ToolTip = text;
        _holdUntil = holdMs > 0 ? Environment.TickCount64 + holdMs : 0;
    }

    /// <summary>
    /// What the timer has to say, which waits its turn while a held message is still up.
    /// Anything the user does speaks over it, since that is an answer to a button press.
    /// </summary>
    void SayFromTick(string text, int holdMs = 0)
    {
        if (Environment.TickCount64 < _holdUntil) return;
        Say(text, holdMs);
    }

    long _holdUntil;

    // ---- запуск -----------------------------------------------------------

    void ConnectHub()
    {
        if (_recovering) { Say(Loc.P("Идёт восстановление связи.", "Restoring the connection."), 6000); return; }

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

        // Gives way to a held message: started from the tick this is the second half of
        // "OpenRGB не отвечает, идёт перезапуск", and the path it reports would push that
        // line out half a second after it appeared.
        SayFromTick(OpenRgbLauncher.Launch(path.Length == 0 ? null : path, _scene.OpenRgbAsAdmin));
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

        Say(_hub.IsReady ? Loc.P("Раскраска запущена.", "Painting started.") : Loc.P("Раскраска запущена, жду OpenRGB.", "Painting started, waiting for OpenRGB."));
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
        _painter.Pause(Loc.P("перезапуск OpenRGB", "restarting OpenRGB"));
        Say(Loc.P("Перезапуск OpenRGB.", "Restarting OpenRGB."), 6000);

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
                Say(string.Format(back ? Loc.P("{0}; подключились заново", "{0}; connected again")
                                       : Loc.P("{0}; сервер не отвечает", "{0}; the server is not responding"), what));
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
        Say(Loc.P("Раскраска остановлена, подсветка погашена.", "Painting stopped, the lighting is off."));
    }

    // ---- геометрия окна ---------------------------------------------------

    void RestoreWindowGeometry()
    {
        Width = Math.Max(WideMinWidth, _scene.WindowWidth);
        Height = Math.Max(700, _scene.WindowHeight);
        MinWidth = WideMinWidth;
        MinHeight = 700;

        _wideWidth = Width;

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
