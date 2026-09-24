using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
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
    readonly PluginHost _plugins = new();

    /// <summary><see cref="RgbHub.PluginGeneration"/> the window last showed.</summary>
    int _pluginGenerationShown = -1;
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

    /// <summary>Waits out the edit before the canvas re-centres itself - see <see cref="AutoFit"/>.</summary>
    readonly DispatcherTimer _autoFit = new() { Interval = TimeSpan.FromMilliseconds(350) };

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
    DockPanel _root = null!;
    StackPanel _titleName = null!;
    StackPanel _canvasTools = null!;
    CheckBox _canvasToggle = null!;
    Button _startButton = null!;
    Button _applyButton = null!;
    Button _cancelButton = null!;
    TextBlock _dirtyText = null!;
    Button _fitButton = null!;
    UIElement _autoFitToggle = null!;
    UIElement _screenToggle = null!;

    /// <summary>Window width with the canvas open, to come back to when it is shown again.</summary>
    double _wideWidth;
    bool? _canvasShown;

    /// <summary>
    /// The narrowest the window may be with the canvas open. Measured in
    /// <see cref="UpdateWideMinWidth"/>; the number here only stands in until then.
    /// </summary>
    double _wideMinWidth = 1100;

    /// <summary>Width of the settings page, the same with the canvas and without it.</summary>
    const double PageWidth = 440;
    Border _dirtyBar = null!;
    Border _updateCard = null!;
    TextBlock _updateText = null!;
    Button _updateClose = null!;
    TextBlock _cropStatus = null!;
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

        // Настройки читаются раньше, чем известно, куда писать лог, поэтому о неудавшемся
        // переносе со старого имени файла сообщается здесь.
        if (Scene.MigrationNote != null) ProbeLog.Log("настройки", Scene.MigrationNote);

        // Раньше всего остального: дальше собираются подписи, а они уже переведённые.
        Loc.Configure(System.IO.Path.Combine(Scene.Folder, "lang"));
        Loc.Load(_scene.Language);

        Title = Loc.T("app.title");

        // BitmapImage берёт из ico только первый кадр, 16 точек, и панель задач показывала
        // его мелким; BitmapFrame отдаёт окну все кадры на выбор (Docs/Интерфейс.md)
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/icon.ico")); }
        catch { /* без иконки окно всё равно работает */ }

        _saved = _scene.Clone();
        _painter = new CasePainter(_hub, _scene);

        // до первой сборки страниц: раздел плагинов показывает найденные папки
        CaseLight.Plugins.PluginApi.Language = Loc.Language;
        _plugins.Scan();
        _hub.AttachPlugins(_plugins);
        _plugins.Apply(_scene.Plugins);

        RestoreWindowGeometry();
        Content = BuildLayout();
        SetupChrome();

        _view.Scene = _scene;
        _view.SelectionChanged += (_, _) => { SyncFixtureList(); ShowFixturePanel(); };
        // Live while the mouse moves: the painter only sets a flag and rebuilds its zones
        // once per frame anyway, so the case follows the fixture as it is dragged.
        _view.FixtureChanged += (_, _) => _painter.Invalidate();

        // The expensive half waits for the mouse to come up. Dragging a fixture edits the
        // scene exactly as typing a coordinate does, so the pending-changes bar has to say
        // so - but rebuilding the panel and serialising the scene are not worth doing a
        // hundred times for one gesture.
        _view.FixtureEdited += (_, _) => { BuildFixturePanel(); Touch(); AutoFit(); };

        // Окно меняет ширину вместе с холстом, и раскладка иначе уезжает за его край.
        _view.SizeChanged += (_, _) => AutoFit();
        _view.TestMoved += (_, _) => PushTestPatch();

        HookPower();

        Loaded += (_, _) =>
        {
            _power.Attach(this);
            SetupTray();
            UpdateChromeMetrics();

            EnsureServer();
            ConnectHub();
            RebuildSections();
            ApplyScreenPreview();
            ApplyCanvasVisibility();
            SyncFixtureList();
            _view.FitToContent();

            if (_scene.StartMinimized) WindowState = WindowState.Minimized;
            if (_scene.StartPaintingOnLaunch) StartPainting();

            // Подписка на питание экрана отдаёт его состояние сразу, блокировка сессии
            // прочитана в конструкторе надзора, но события ни то, ни другое не рождает:
            // запуск в заблокированной сессии иначе начинался бы с раскраски.
            ApplyPowerState(_power.State);

            // Не ожидается: ответ может идти секунды, а подсветке это время нужнее.
            if (_scene.CheckUpdates) _ = AnnounceUpdateAsync();
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

        _autoFit.Tick += (_, _) => { _autoFit.Stop(); _view.FitToContent(); };

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

            // Порядок важен: пока поток раскраски жив, его очередной кадр уходит следом за
            // гашением и корпус остаётся светиться. Само гашение здесь, а не в Stop, потому
            // что при снятой галке подсветка должна остаться гореть - и после теста
            // размещения, когда раскраска и не запускалась.
            _painter.Stop(blackout: false);

            if (_scene.OffOnExit)
            {
                _hub.Blackout();
                RgbHub.Settle();
            }

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

            // устройства плагинов возвращаются к своим эффектам: держать кадр после выхода некому
            _plugins.Dispose();
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

        // The title bar is the first row of the same grid, so what stands in it lines up
        // with the column below: the program name over the sections, the painting over the
        // page, the canvas switches over the canvas.
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- заголовок
        _titleName = BuildTitleName();
        grid.Children.Add(_titleName);

        grid.Children.Add(BuildTitleActions());

        _canvasTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, _captionWidth + CaptionGap, 0)
        };

        _screenToggle = BuildScreenToggle();
        _autoFitToggle = BuildAutoFitToggle();
        _fitButton = BuildFitButton();
        _canvasTools.Children.Add(_screenToggle);
        _canvasTools.Children.Add(_autoFitToggle);
        _canvasTools.Children.Add(_fitButton);

        WindowChrome.SetIsHitTestVisibleInChrome(_canvasTools, true);
        Grid.SetColumn(_canvasTools, 2);
        grid.Children.Add(_canvasTools);

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

        _rail = new DockPanel { Margin = new Thickness(6, 12, 6, 12) };
        _rail.Children.Add(_nav);

        Grid.SetRow(_rail, 1);
        Grid.SetColumn(_rail, 0);
        grid.Children.Add(_rail);

        // ---- по центру: страница выбранного раздела и полоса применения
        var page = new Grid { Margin = new Thickness(0, 12, 12, 12) };
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _pageHost = new ContentControl();
        Grid.SetRow(_pageHost, 0);
        page.Children.Add(_pageHost);

        // A new release is an offer, not a fault, so it stands on a card of its own rather
        // than in the line that reports a lost connection.
        _updateCard = BuildUpdateCard();
        Grid.SetRow(_updateCard, 1);
        page.Children.Add(_updateCard);

        _dirtyBar = BuildDirtyBar();
        Grid.SetRow(_dirtyBar, 2);
        page.Children.Add(_dirtyBar);

        Grid.SetRow(page, 1);
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

        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        // ---- низ: статус
        _status = new TextBlock
        {
            Foreground = Ui.FgDim,
            FontSize = Ui.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 8),

            // One line always: a message with a file path in it would otherwise wrap to
            // three and lift the whole bottom bar, and the bar moving about under the
            // buttons is worse than a tail behind an ellipsis. The full text is in the
            // tooltip, so nothing said here is out of reach.
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // The status line is kept out of the grid on purpose. Spanning it across the columns
        // made its own width a claim on them, and the first column is auto-sized: one status
        // line about a lost connection stretched that column until the settings page hung
        // off the right edge of the window. Docked here it takes what the window has, no more.
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(grid);

        _root = root;
        return root;
    }

    /// <summary>
    /// The screen switch, built apart from the bar so a language change can put a fresh one
    /// in its place: its tooltip is inside the element, and there is nothing to reassign.
    /// </summary>
    UIElement BuildScreenToggle()
    {
        // Over the canvas rather than in the settings: it is a way of looking at the
        // layout, switched on and off while working on it, not something to set once.
        var toggle = Ui.Check(Loc.T("bar.screen"), _scene.ShowScreen, v =>
        {
            if (_rebuildingUi) return;

            _scene.ShowScreen = v;
            ApplyScreenPreview();
            Touch();
        }, Loc.T("bar.screen.note"));

        if (toggle is FrameworkElement box) box.Margin = new Thickness(12, 0, 0, 0);
        return toggle;
    }

    /// <summary>
    /// The canvas fit, an icon in the corner rather than a caption among the buttons on the
    /// left: it is a way of looking at the layout, like the two switches beside it, and not
    /// something the painting does.
    /// </summary>
    Button BuildFitButton()
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "\uE799", FontFamily = Ui.IconFont, FontSize = 12 },
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,

            // всплывающая подсказка наследует шрифт значка, а в нём букв нет
            ToolTip = new TextBlock
            {
                Text = Loc.T("bar.fit"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            }
        };

        button.Click += (_, _) => _view.FitToContent();
        return button;
    }

    /// <summary>
    /// Keeps the whole layout in view by itself. Built apart from the bar for the same
    /// reason as the screen switch: its explanation lives inside the element.
    /// </summary>
    UIElement BuildAutoFitToggle()
    {
        var toggle = Ui.Check(Loc.T("bar.autofit"), _scene.AutoFitCanvas, v =>
        {
            if (_rebuildingUi) return;

            _scene.AutoFitCanvas = v;
            if (v) _view.FitToContent();
            Touch();
        }, Loc.T("bar.autofit.note"));

        if (toggle is FrameworkElement box) box.Margin = new Thickness(12, 0, 0, 0);
        return toggle;
    }

    Border BuildUpdateCard()
    {
        _updateText = new TextBlock
        {
            Foreground = Ui.Fg,
            FontSize = Ui.TextSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        // Закрытие живёт одну сессию: проверка идёт при запуске, поэтому следующий холодный
        // старт покажет карточку снова, пока релиз новее.
        _updateClose = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = Ui.IconFont, FontSize = 11 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = Loc.T("update.hide")
        };
        _updateClose.Click += (_, _) => _updateCard.Visibility = Visibility.Collapsed;

        var dock = new DockPanel();
        DockPanel.SetDock(_updateClose, Dock.Right);
        dock.Children.Add(_updateClose);
        dock.Children.Add(_updateText);

        var card = Ui.Card(dock);
        card.Padding = new Thickness(12);
        card.Margin = new Thickness(0, 10, 0, 0);
        card.Visibility = Visibility.Collapsed;
        return card;
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
        BuildOpenRgbSection();
        BuildPluginsSection();
        BuildDevicesSection();
        BuildCaptureSection();
        BuildCropSection();
        BuildBrightnessSection();
        BuildColorsSection();
        BuildTestSection();
        BuildPowerSection();
        BuildAboutSection();

        selected = Math.Min(selected, _pages.Count - 1);

        // The selection change arrives while the rebuild guard is still up, so the page is
        // handed over here rather than left to the handler.
        _nav.SelectedIndex = selected;
        _pageHost.Content = _pages[selected];

        RefreshBarToggles();

        _rebuildingUi = false;

        // the settings may have arrived from a cancel, an import or a reset, not from a checkbox
        ApplyScreenPreview();
        ApplyCanvasVisibility();

        // подписи над холстом могли смениться вместе с языком, а с ними и его ширина
        UpdateWideMinWidth();
        ApplyBackdrop();
    }

    /// <summary>
    /// Puts the two switches outside the settings pages back in step with the settings.
    ///
    /// They stand in the title bar rather than on a page, so a
    /// wholesale rebuild - cancel, import, reset - would otherwise leave them showing what
    /// was set before it. The screen switch carries its explanation inside itself and is
    /// replaced whole rather than relabelled.
    /// </summary>
    void RefreshBarToggles()
    {
        _canvasToggle.IsChecked = _scene.ShowCanvas;

        _screenToggle = Replace(_screenToggle, BuildScreenToggle());
        _autoFitToggle = Replace(_autoFitToggle, BuildAutoFitToggle());

        _fitButton = Replace(_fitButton, BuildFitButton());
    }

    /// <summary>
    /// Puts a freshly built control in the old one's place in the bar.
    ///
    /// These carry their explanations inside themselves, so a language change and a cancel
    /// alike are answered by building them again rather than by reassigning anything.
    /// </summary>
    T Replace<T>(T old, T fresh) where T : UIElement
    {
        int at = _canvasTools.Children.IndexOf(old);
        if (at < 0) return old;

        _canvasTools.Children.RemoveAt(at);
        _canvasTools.Children.Insert(at, fresh);
        return fresh;
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

        var backdropBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var backdrop in Backdrops) backdropBox.Items.Add(Loc.T(BackdropKey(backdrop)));
        backdropBox.SelectedIndex = Math.Max(0, Array.IndexOf(Backdrops, _scene.Backdrop));
        backdropBox.SelectionChanged += (_, _) =>
        {
            if (_rebuildingUi || backdropBox.SelectedIndex < 0) return;

            // список поднимает событие и при входе в дерево, с тем же значением
            var chosen = Backdrops[backdropBox.SelectedIndex];
            if (chosen == _scene.Backdrop) return;

            _scene.Backdrop = chosen;
            ApplyBackdrop();
            Touch();
        };
        panel.Children.Add(Ui.Labeled(Loc.T("main.backdrop"), backdropBox, Loc.T("main.backdrop.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.tray"), _scene.MinimizeToTray, v => { _scene.MinimizeToTray = v; Touch(); },
            Loc.T("main.tray.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.startmin"), _scene.StartMinimized, v => { _scene.StartMinimized = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("main.startup")));
        panel.Children.Add(Ui.Check(Loc.T("main.autostart"), Autostart.IsEnabled(), v => Say(Autostart.Set(v)),
            Loc.T("main.autostart.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.autopaint"), _scene.StartPaintingOnLaunch, v => { _scene.StartPaintingOnLaunch = v; Touch(); }));

        panel.Children.Add(Ui.Header(Loc.T("main.settings"),
            Loc.T("main.settings.note")));
        panel.Children.Add(Ui.Row(
            Ui.Btn(Loc.T("main.export"), ExportSettings),
            Ui.Btn(Loc.T("main.import"), ImportSettings),
            Ui.Btn(Loc.T("main.reset"), ResetSettings),
            Ui.HelpIcon(Loc.T("main.reset.note"))));

        panel.Children.Add(Ui.Header(Loc.T("main.logs"), Loc.T("main.logs.note")));
        panel.Children.Add(Ui.Check(Loc.T("main.log"), _scene.WriteLog, v =>
        {
            _scene.WriteLog = v;
            ProbeLog.Configure(Scene.LogPath, v);
            Touch();
        }));
        panel.Children.Add(Ui.PathLink(Scene.LogPath));
    });

    /// <summary>
    /// Everything about the server the lighting is driven through, including what to do
    /// with it after a wake.
    ///
    /// A section of its own because these settings are about a neighbouring program rather
    /// than about this one, and in «Основное» they took up more room than everything else
    /// there together.
    /// </summary>
    void BuildOpenRgbSection() => AddSection(Loc.T("tab.openrgb"), "\uE968", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("openrgb.start")));
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

        panel.Children.Add(Ui.Header(Loc.T("power.wake")));

        var wakeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        wakeBox.Items.Add(Loc.T("power.wake.nothing"));
        wakeBox.Items.Add(Loc.T("power.wake.rescan"));
        wakeBox.Items.Add(Loc.T("power.wake.rescanrestart"));
        wakeBox.Items.Add(Loc.T("power.wake.restart"));
        wakeBox.SelectedIndex = Math.Max(0, Array.IndexOf(WakeModes, _scene.WakeRecovery));
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
    /// Plugins: support for devices the OpenRGB server does not drive, one folder each in the
    /// plugins folder next to the program.
    ///
    /// A plugin found there is not started until its box is ticked: starting it runs its code.
    /// Its name and description are only known after that, so until then the list shows the
    /// folder.
    /// </summary>
    void BuildPluginsSection() => AddSection(Loc.T("tab.plugins"), "\uEA86", panel =>
    {
        panel.Children.Add(Ui.Header(Loc.T("plugins.list"), Loc.T("plugins.list.note")));

        var entries = _plugins.Entries;
        if (entries.Count == 0) panel.Children.Add(Ui.Note(Loc.T("plugins.none")));

        foreach (var entry in entries)
        {
            var plugin = entry.Plugins.FirstOrDefault();
            string title = plugin == null ? entry.Id : plugin.Name;
            bool on = _scene.Plugins.Contains(entry.Id, StringComparer.OrdinalIgnoreCase);

            panel.Children.Add(Ui.Check(title, on, v =>
            {
                if (_rebuildingUi) return;

                _scene.Plugins = v
                    ? [.. _scene.Plugins.Where(id => !string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)), entry.Id]
                    : _scene.Plugins.Where(id => !string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToArray();

                _plugins.Apply(_scene.Plugins);
                Touch();
            }, plugin?.Description));

            string state = entry.Error != "" ? entry.Error
                : !entry.Running ? Loc.T("plugins.off")
                : string.Format(Loc.T("plugins.devices"), entry.Plugins.Sum(DeviceCount));
            panel.Children.Add(Ui.Note(state));

            foreach (var running in entry.Plugins)
            foreach (var (name, problem) in Problems(running))
                panel.Children.Add(Ui.Warning(name + ": " + problem));
        }

        try { System.IO.Directory.CreateDirectory(PluginHost.Root); }
        catch { /* папка программы бывает закрыта для записи, путь всё равно показывается */ }

        panel.Children.Add(Ui.Header(Loc.T("plugins.folder"), Loc.T("plugins.folder.note")));
        panel.Children.Add(Ui.PathLink(PluginHost.Root));
        panel.Children.Add(Ui.Row(Ui.Btn(Loc.T("plugins.rescan"), () =>
        {
            _plugins.Scan();
            _plugins.Apply(_scene.Plugins);
            RebuildSections();
        })));
    });

    /// <summary>The devices of a plugin that report a problem, with its text.</summary>
    static (string Name, string Problem)[] Problems(CaseLight.Plugins.ILightPlugin plugin)
    {
        try { return plugin.Devices.Where(d => d.Problem != "").Select(d => (d.Name, d.Problem)).ToArray(); }
        catch { return []; }
    }

    static int DeviceCount(CaseLight.Plugins.ILightPlugin plugin)
    {
        try { return plugin.Devices.Count; }
        catch { return 0; }
    }

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

        // Верх шкалы — не число, а «без ограничения»: там настройка перестаёт задавать темп,
        // и остаётся только собственный пол цикла раскраски. Ограничение оплачивается
        // задержкой, потому что кадр, пришедший раньше срока, отбрасывается, а не придерживается.
        panel.Children.Add(Ui.Slider(Loc.T("capture.fps"), _scene.MaxFps <= 0 ? FpsFree : _scene.MaxFps,
            10, FpsFree, 1,
            v => { _scene.MaxFps = v >= FpsFree ? 0 : (int)v; Touch(); }, "",
            Loc.T("capture.fps.note"),
            format: v => v >= FpsFree ? Loc.T("capture.fps.free") : v.ToString("0")));

        // Отключённый ползунок в этой теме почти не отличается от живого, поэтому он ещё и
        // приглушается, как настройки фигуры, взятые из общих.
        var radius = Ui.Slider(Loc.T("capture.radius"), _scene.SampleRadiusMm, 1, 100, 1,
            v => { _scene.SampleRadiusMm = Math.Max(1, v); ShowSampleArea(); Touch(); }, Loc.T("unit.mm"),
            Loc.T("capture.radius.note"), enabled: !_scene.SampleBySize);
        if (_scene.SampleBySize) radius.Opacity = 0.45;
        panel.Children.Add(radius);

        // Переключение меняет смысл размеров всех фигур, поэтому они пересчитываются сразу,
        // а страница и панель фигуры строятся заново: у ползунка и полей размера другое состояние.
        panel.Children.Add(Ui.Check(Loc.T("capture.bysize"), _scene.SampleBySize, v =>
        {
            if (_rebuildingUi) return;

            _scene.SetSampleBySize(v);
            RebuildSections();
            BuildFixturePanel();
            Touch();
            AutoFit();
        }, Loc.T("capture.bysize.note")));

        // Один ползунок на две противоположные вещи, ноль посередине: включить обе сразу
        // нельзя, потому что вторая отменяла бы первую. Шкала целая, в процентах: дробный
        // шаг от -1 не попадает в ноль ровно, и выключенное состояние оказывалось
        // недостижимым.
        panel.Children.Add(Ui.Slider(Loc.T("capture.sharpness"), _scene.Sharpness,
            -FrameFilter.MaxPercent, FrameFilter.MaxPercent, 1,
            v => { _scene.Sharpness = (int)Math.Round(v); Touch(); }, "",
            Loc.T("capture.sharpness.note"),
            format: DescribeSharpness));

        panel.Children.Add(Ui.Header(Loc.T("capture.stats")));
        panel.Children.Add(BuildStats());
    });

    /// <summary>Each half of the scale says which of the two is running and how far.</summary>
    static string DescribeSharpness(double v) =>
        v <= -1 ? string.Format(Loc.T("capture.blur"), (int)Math.Round(-v)) :
        v >= 1 ? string.Format(Loc.T("capture.sharp"), (int)Math.Round(v)) :
        Loc.T("off");

    /// <summary>Where the frame-rate slider stops being a limit and becomes «no limit».</summary>
    const double FpsFree = 145;

    void BuildCropSection() => AddSection(Loc.T("tab.crop"), "\uE123", panel =>
    {
        panel.Children.Add(Ui.Note(Loc.T("crop.head")));

        panel.Children.Add(Ui.Check(Loc.T("crop.enable"), _scene.AdaptiveCrop, v =>
        {
            if (_rebuildingUi) return;

            _scene.AdaptiveCrop = v;
            Touch();
            RebuildSections();          // всё ниже включается и гаснет вместе с ним
        }, Loc.T("crop.enable.note")));

        _cropStatus = Ui.Note("");
        panel.Children.Add(_cropStatus);
        UpdateCropStatus();

        bool on = _scene.AdaptiveCrop;

        panel.Children.Add(Ui.Check(Loc.T("crop.vertical"), _scene.CropVertical,
            v => { _scene.CropVertical = v; Touch(); }, Loc.T("crop.vertical.note"), enabled: on));
        panel.Children.Add(Ui.Check(Loc.T("crop.horizontal"), _scene.CropHorizontal,
            v => { _scene.CropHorizontal = v; Touch(); }, Loc.T("crop.horizontal.note"), enabled: on));
        panel.Children.Add(Ui.Check(Loc.T("crop.stretch"), _scene.CropStretch,
            v => { _scene.CropStretch = v; Touch(); }, Loc.T("crop.stretch.note"), enabled: on));

        panel.Children.Add(Ui.Slider(Loc.T("crop.min"), _scene.CropMinPercent, 0, 10, 0.5,
            v => { _scene.CropMinPercent = v; Touch(); }, "%", Loc.T("crop.min.note"), enabled: on));
        panel.Children.Add(Ui.Slider(Loc.T("crop.max"), _scene.CropMaxPercent, 5, 40, 1,
            v => { _scene.CropMaxPercent = v; Touch(); }, "%", Loc.T("crop.max.note"), enabled: on));
        panel.Children.Add(Ui.Slider(Loc.T("crop.level"), _scene.CropBlackLevel, 0, 48, 1,
            v => { _scene.CropBlackLevel = (int)v; Touch(); }, "", Loc.T("crop.level.note"), enabled: on));
        panel.Children.Add(Ui.Slider(Loc.T("crop.overlook"), _scene.CropOverlookPercent, 0, 10, 0.5,
            v => { _scene.CropOverlookPercent = v; Touch(); }, "%", Loc.T("crop.overlook.note"), enabled: on));
        panel.Children.Add(Ui.Slider(Loc.T("crop.hold"), _scene.CropHoldMs / 1000.0, 0.1, 3.0, 0.05,
            v => { _scene.CropHoldMs = v * 1000.0; Touch(); }, Loc.T("unit.s"), Loc.T("crop.hold.note"), enabled: on));
        panel.Children.Add(Ui.Slider(Loc.T("crop.inset"), _scene.CropInsetPercent, 0, 3, 0.1,
            v => { _scene.CropInsetPercent = v; Touch(); }, "%", Loc.T("crop.inset.note"),
            format: v => v <= 0 ? Loc.T("off") : v.ToString("0.0"), enabled: on));
    });

    /// <summary>
    /// What the detector sees right now, live.
    ///
    /// Without it the settings are guesswork: the numbers only mean something against the
    /// material actually on screen, and the case alone does not say whether a bar was found
    /// or merely suspected.
    /// </summary>
    void UpdateCropStatus()
    {
        if (_cropStatus == null) return;

        if (!_scene.AdaptiveCrop)
        {
            _cropStatus.Text = Loc.T("crop.status.off");
            return;
        }

        var r = _painter.Crop;
        bool v = r.Y0 > 0.001, h = r.X0 > 0.001;

        // Целые предложения на каждый случай, а не сборка из кусков: порядок слов в другом
        // языке другой, и в склейке переводчик его не поменяет.
        _cropStatus.Text =
            v && h ? string.Format(Loc.T("crop.status.both"), r.Y0 * 100, r.X0 * 100) :
            v ? string.Format(Loc.T("crop.status.v"), r.Y0 * 100) :
            h ? string.Format(Loc.T("crop.status.h"), r.X0 * 100) :
            Loc.T("crop.status.none");
    }

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
    });

    /// <summary>The recovery modes in the order the list shows them, gentlest first.</summary>
    static readonly WakeRecovery[] WakeModes =
    {
        WakeRecovery.Nothing, WakeRecovery.Rescan, WakeRecovery.RescanThenRestart, WakeRecovery.RestartServer
    };

    void BuildAboutSection() => AddSection(Loc.T("tab.about"), "\uE897", panel =>
    {
        panel.Children.Add(Ui.Header("CaseLight " + AppVersion));

        panel.Children.Add(Ui.Note(Loc.T("about.text")));

        panel.Children.Add(Ui.Note(Loc.T("about.text2")));

        panel.Children.Add(Ui.Note(Loc.T("about.text3")));

        panel.Children.Add(Ui.Check(Loc.T("about.updates"), _scene.CheckUpdates,
            v => { _scene.CheckUpdates = v; Touch(); }));

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
        _plugins.Apply(_scene.Plugins);

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
            _plugins.Apply(_scene.Plugins);

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

    /// <summary>
    /// Puts every setting back to its standard value, leaving the layout alone.
    ///
    /// Applied live like any other edit rather than written straight to disk: a reset moves
    /// several pages at once, and «Отмена» has to be able to take it back. What lives
    /// outside the settings file goes with it - the autostart entry in the registry and the
    /// scheduler task, both of which the standard values say are off.
    /// </summary>
    void ResetSettings()
    {
        if (MessageBox.Show(Loc.T("main.reset.confirm"), Loc.T("main.reset"),
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _scene.ResetToDefaults();

        ProbeLog.Configure(Scene.LogPath, _scene.WriteLog);
        if (Autostart.IsEnabled()) Autostart.Set(false);

        // The task is «run as administrator» by another name, so it follows that setting
        // down exactly as it does when the checkbox is cleared by hand.
        if (OpenRgbTask.Exists()) OpenRgbTask.Delete();

        RebuildSections();
        SyncFixtureList();
        _painter.Invalidate();
        _view.InvalidateVisual();
        UpdateDirtyBar();

        Say(Loc.P("Настройки сброшены. Проверьте и нажмите «Применить».",
                  "Settings reset. Check them and press «Apply»."));
    }

    // ---- обновления -------------------------------------------------------

    string? _updateUrl;
    string _updateVersion = "";

    /// <summary>
    /// Says once, on the way in, that a newer release exists. Silent otherwise - including
    /// when the check itself failed, which is not news the user asked for.
    ///
    /// Started from the dispatcher and never configured away from it, so the continuation
    /// after the request comes back on the interface thread and can touch the window.
    /// </summary>
    async System.Threading.Tasks.Task AnnounceUpdateAsync()
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0, 0);

        var found = await UpdateCheck.FindNewerAsync(current);
        if (found == null) return;

        _updateUrl = found.Value.Url;
        _updateVersion = found.Value.Version.ToString(3);

        string text = string.Format(Loc.T("update.available"), _updateVersion);
        ProbeLog.Log(Loc.P("обновление", "update"), text);

        // Only when the program opened into the tray: then the window is not on screen and
        // the card has nobody to show itself to. With the window open the card is enough,
        // and a balloon over it would say the same thing twice. The card is shown either
        // way - a balloon that was missed leaves nothing behind.
        if (_scene.StartMinimized && _tray is { Visible: true })
        {
            _tray.BalloonTipClicked -= OnUpdateBalloonClicked;
            _tray.BalloonTipClicked += OnUpdateBalloonClicked;
            _tray.ShowBalloonTip(10000, "CaseLight", text, System.Windows.Forms.ToolTipIcon.Info);
        }

        ShowUpdateCard();
    }

    /// <summary>Fills the card in the current language; called again after a language change.</summary>
    void ShowUpdateCard()
    {
        if (_updateUrl == null) return;

        _updateClose.ToolTip = Loc.T("update.hide");

        _updateText.Inlines.Clear();
        _updateText.Inlines.Add(string.Format(Loc.T("update.available"), _updateVersion) + " ");

        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run(Loc.T("update.open")));
        Ui.StyleLink(link);
        link.Click += (_, _) => OpenUpdatePage();
        _updateText.Inlines.Add(link);

        _updateCard.Visibility = Visibility.Visible;
    }

    void OnUpdateBalloonClicked(object? sender, EventArgs e) => OpenUpdatePage();

    void OpenUpdatePage()
    {
        if (_updateUrl != null) Ui.OpenUrl(_updateUrl);
    }

    // ---- питание, трей, статус --------------------------------------------

    void HookPower() => _power.Changed += (_, state) => ApplyPowerState(state);

    /// <summary>
    /// Works out what the state of the machine means for the painting.
    ///
    /// The watcher only reports; which of these states counts as «никто не смотрит» is a
    /// matter of this program's settings, so the answer belongs here.
    /// </summary>
    void ApplyPowerState(PowerState state)
    {
        string? reason =
            state.Suspended && _scene.OffOnSuspend ? Loc.P("сон", "sleep") :
            state.Locked && _scene.OffOnLock ? Loc.P("блокировка", "locked") :
            state.DisplayOff && _scene.OffOnDisplayOff ? Loc.P("экран выключен", "display off") :
            null;

        if (state.Suspended) _wokeUp = false;

        // Раскраска, остановленная кнопкой, событию питания не отвечает: гасить нечего,
        // и будить сервер ради погашенного корпуса незачем.
        if (!_painter.IsRunning) return;

        // Погашенный экран не отдаёт композицию вообще, поэтому свой захват на это время
        // снимается: голодать и перебирать источники впустую незачем.
        _painter.SuspendCapture(state.DisplayOff);

        // Экран погашен, а гасить подсветку не просили: держим последний кадр. Поток
        // раскраски повторяет его сам, захват для этого не нужен.
        _painter.Freeze(state.DisplayOff && !_scene.OffOnDisplayOff);

        if (reason != null)
        {
            _painter.Pause(reason);

            // Запись возвращается, когда байты легли в сокет, а сервер отдаёт их по USB
            // позже; машина же засыпает сразу после возврата из обработчика.
            if (state.Suspended) RgbHub.Settle();
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
    }

    /// <summary>
    /// Brings the lighting back after sleep.
    ///
    /// Resuming alone is not enough: the controllers were re-enumerated while the machine
    /// slept, and the server that stayed up keeps writing into handles that lead nowhere -
    /// it reports success while the case shows its power-on pattern. The server has to open
    /// its devices anew: by a rescan where it supports one, by a restart otherwise.
    /// </summary>
    void RecoverAfterWake()
    {
        var mode = _scene.WakeRecovery;

        if (mode == WakeRecovery.Nothing)
        {
            _painter.Resume(_scene.ResumeDelayMs);
            return;
        }

        _recovering = true;

        // Nothing was asked of the server since before sleep, so the list still counts the
        // devices that were there - the measure a rescan is checked against.
        int devicesBefore = _hub.ServerDeviceCount;

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
                // A server on protocol 6 lets a short list be caught and topped up by another
                // rescan, so recovery can start at once. An older one has only the restart,
                // and the restart only the pause to keep it off a bus still settling.
                bool canRescan = _hub.CanRescan;
                System.Threading.Thread.Sleep(canRescan ? _scene.ResumeDelayMs : Math.Max(2000, _scene.ResumeDelayMs));

                if (mode is WakeRecovery.Rescan or WakeRecovery.RescanThenRestart)
                {
                    back = RescanServer(devicesBefore, out what);

                    if (!back && mode == WakeRecovery.RescanThenRestart)
                    {
                        ProbeLog.Log("OpenRGB", Loc.P("пересканирование не помогло, перезапуск: ", "the rescan did not help, restarting: ") + what);
                        what = RestartServer();
                        back = WaitForDevices(wanted: devicesBefore);
                    }
                }
                else
                {
                    what = RestartServer();
                    back = WaitForDevices(wanted: devicesBefore);
                }

                // A restart that came back short is topped up by a rescan, which is seconds,
                // instead of being left short or restarted again.
                if (back && _hub.ServerDeviceCount < devicesBefore && _hub.CanRescan)
                    back = RescanServer(devicesBefore, out what);
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
                    ? string.Format(Loc.P("{0}; подсветка восстановлена", "{0}; the lighting is back"), what)
                    : mode == WakeRecovery.Rescan
                        ? string.Format(Loc.P("{0}; остаётся перезапуск кнопкой «{1}»",
                                              "{0}; a restart is left, with the «{1}» button"), what, Loc.T("power.restartnow"))
                        : string.Format(Loc.P("{0}; сервер не отвечает, нажмите «Переподключиться»",
                                              "{0}; the server is not responding, press «Reconnect»"), what));

                BuildFixturePanel();

                _painter.Resume(0);
                if (_paintingWanted && !_painter.IsRunning) _painter.Start();
            });
        });
    }

    /// <summary>
    /// Wake recovery without a restart: the server detects its devices anew, and the new
    /// controller objects hold new handles.
    ///
    /// Success is judged by what came back, not by what the server says - it said "success"
    /// over dead handles too, which is the very state being recovered from. At least as many
    /// devices as there were before sleep have to be back in the list; the end of detection
    /// is not waited for once they are.
    /// </summary>
    bool RescanServer(int devicesBefore, out string what)
    {
        if (!_hub.IsConnected) _hub.Connect(force: true);

        if (!_hub.CanRescan)
        {
            what = Loc.P("OpenRGB не поддерживает пересканирование, нужна версия 1.0 или новее",
                         "OpenRGB cannot rescan, version 1.0 or later is needed");
            return false;
        }

        // no devices before sleep leaves nothing to compare with; one found is then enough
        int wanted = Math.Max(1, devicesBefore);

        for (int attempt = 1; ; attempt++)
        {
            if (_hub.Rescan(wanted))
            {
                what = Loc.P("устройства найдены заново", "the devices were found again");
                return true;
            }

            what = string.Format(Loc.P("после поиска найдено устройств: {0} из {1}", "devices found after detection: {0} of {1}"),
                                 _hub.ServerDeviceCount, devicesBefore);

            // A short list right after waking is a bus still settling, not a device gone:
            // looking again a moment later is quicker than holding every wake back by a pause.
            if (attempt >= RescanAttempts) return false;

            ProbeLog.Log("OpenRGB", what + Loc.P(", повтор", ", again"));
            System.Threading.Thread.Sleep(RescanRetryMs);
        }
    }

    const int RescanAttempts = 3;
    const int RescanRetryMs = 1000;

    /// <returns>What happened, ready for the status line.</returns>
    string RestartServer()
    {
        // The connection goes first: a restart would otherwise be writing into a dying
        // process.
        _hub.Dispose();

        return OpenRgbLauncher.Restart(
            string.IsNullOrWhiteSpace(_scene.OpenRgbPath) ? null : _scene.OpenRgbPath,
            _scene.OpenRgbAsAdmin);
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

        // раскраска останавливается и сама: тест, восстановление после сна
        UpdateStartButton();

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
        UpdateCropStatus();

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
        AutoFit();
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
        // Плагины не зависят от сервера и восстановления после сна. Список забирает и поток
        // раскраски, поэтому смена замечается по поколению, а не по ответу SyncPlugins.
        _hub.SyncPlugins();
        if (_hub.PluginGeneration != _pluginGenerationShown)
        {
            _pluginGenerationShown = _hub.PluginGeneration;
            RebuildSections();
            BuildFixturePanel();
            _painter.Invalidate();
        }

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

        int before = _hub.ServerDeviceCount;
        if (!_hub.TryRefresh()) return;

        if (_hub.ServerDeviceCount != before)
        {
            // still filling up - start the count again rather than settle on a partial list
            _settlePolls = SettlePolls;
            Say(_hub.Status);
            BuildFixturePanel();
            _painter.Invalidate();
        }
        else if (_hub.ServerDeviceCount == 0)
        {
            Say(Loc.P("OpenRGB подключён, устройств пока нет: идёт поиск.", "OpenRGB connected, no devices yet: still looking."));
        }
        else
        {
            _settlePolls--;
        }
    }

    /// <summary>
    /// Brings the whole layout back into view, when the switch under the canvas asks for it.
    ///
    /// Held back by a timer rather than done on the spot: a coordinate typed into the
    /// fixture panel arrives a character at a time, and a canvas that re-centres on every
    /// keystroke cannot be typed into.
    /// </summary>
    void AutoFit()
    {
        if (!_scene.AutoFitCanvas) return;

        _autoFit.Stop();
        _autoFit.Start();
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

        if (show)
        {
            _canvasHost.Visibility = Visibility.Visible;
            _canvasTools.Visibility = Visibility.Visible;
            _canvasColumn.Width = new GridLength(1, GridUnitType.Star);

            MaxWidth = double.PositiveInfinity;
            UpdateWideMinWidth();
            if (IsLoaded && WindowState == WindowState.Normal)
                Width = Math.Max(_wideMinWidth, _wideWidth);
            return;
        }

        if (IsLoaded && WindowState == WindowState.Normal && ActualWidth >= _wideMinWidth)
            _wideWidth = ActualWidth;

        double narrow = NarrowWidth();

        _canvasHost.Visibility = Visibility.Collapsed;
        _canvasTools.Visibility = Visibility.Collapsed;
        _canvasColumn.Width = new GridLength(0);
        _canvasColumn.MinWidth = 0;

        MinWidth = 0;
        Width = narrow;
        MinWidth = MaxWidth = narrow;
    }

    /// <summary>
    /// Keeps the canvas at least as wide as the switches standing over it in the title bar,
    /// and the window wide enough for that canvas.
    ///
    /// Measured rather than fixed: the captions change with the language, and the caption
    /// buttons the switches keep clear of change with the scale of the screen.
    /// </summary>
    void UpdateWideMinWidth()
    {
        if (!_scene.ShowCanvas) return;

        _canvasTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double canvas = _canvasTools.DesiredSize.Width;

        _canvasColumn.MinWidth = canvas;
        _wideMinWidth = LeftColumnWidth() + PageWidth + canvas;
        MinWidth = _wideMinWidth;
    }

    /// <summary>
    /// What is left of the window once the canvas is gone: the left column and the settings
    /// page. Added up rather than asked of the layout, because the point is a width that
    /// does not depend on what is written in the window.
    ///
    /// The page column already holds the page's right margin, and the content covers the
    /// whole window frame, so that margin is the gap at the window edge.
    /// </summary>
    double NarrowWidth() => LeftColumnWidth() + PageWidth;

    /// <summary>
    /// The auto-sized first column: the wider of the section rail and the program name
    /// over it.
    ///
    /// Measured, not read: a rebuild of the sections changes the captions, and before the
    /// window is shown nothing has been laid out at all. DesiredSize covers the margins
    /// either way.
    /// </summary>
    double LeftColumnWidth()
    {
        var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _rail.Measure(any);
        _titleName.Measure(any);
        return Math.Max(_rail.DesiredSize.Width, _titleName.DesiredSize.Width);
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

        // Предупреждения плагинов складываются в момент смены состояния устройства и на
        // новом языке появятся со следующей сменой; описания перечитываются при пересборке ниже.
        CaseLight.Plugins.PluginApi.Language = Loc.Language;

        Title = Loc.T("app.title");
        _canvasToggle.Content = Loc.T("nav.canvas");
        UpdateStartButton();
        _applyButton.Content = Loc.T("bar.apply");
        _cancelButton.Content = Loc.T("bar.cancel");
        _dirtyText.Text = Loc.T("bar.dirty");

        SetupTray();
        RebuildSections();          // заодно пересобирает переключатель под холстом
        SyncFixtureList();
        BuildFixturePanel();

        if (_updateCard.Visibility == Visibility.Visible) ShowUpdateCard();

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

    void TogglePainting()
    {
        if (_painter.IsRunning) StopPainting();
        else StartPainting();
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
        UpdateStartButton();

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
    /// <param name="wanted">
    /// How many devices there were before sleep; once that many are back the wait is over,
    /// detection finished or not. Zero leaves only the checks below.
    /// </param>
    bool WaitForDevices(int attempts = 90, int wanted = 0)
    {
        int last = -1, stable = 0;
        long since = Environment.TickCount64;

        for (int i = 0; i < attempts; i++)
        {
            // taken before the read, so a detection that ends during it is not missed
            bool detectionEnded = _hub.LastDetectionEndTicks >= since;

            if (!_hub.IsConnected) _hub.Connect(force: true);
            else _hub.Refresh();

            int count = _hub.ServerDeviceCount;

            // Everything that was there before is back: the rest of detection only looks for
            // hardware this machine was not using.
            if (wanted > 0 && count >= wanted) return true;

            // A server on protocol 6 says when detection is over, and a list read after
            // that is the whole list.
            if (detectionEnded && count > 0) return true;

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
        UpdateStartButton();
        Say(Loc.P("Раскраска остановлена, подсветка погашена.", "Painting stopped, the lighting is off."));
    }

    // ---- геометрия окна ---------------------------------------------------

    void RestoreWindowGeometry()
    {
        Width = Math.Max(_wideMinWidth, _scene.WindowWidth);
        Height = Math.Max(700, _scene.WindowHeight);
        MinWidth = _wideMinWidth;
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
