using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaseLight.Model;
using CaseLight.Render;
using CaseLight.View;

using CaseLight.Core.Text;

namespace CaseLight;

/// <summary>
/// Colour calibration: the test colours and the matrix the scene or a fixture is matched
/// with. The same set of sliders stands in the section for the scene and on the colour tab
/// of a fixture with its own colour.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Test colours, in the order they are worth going through: white and grey for the
    /// balance, the three primaries for the hue and saturation sliders, then the mixtures
    /// that show whether the primaries came out right. The same set as in Rimlight.
    /// </summary>
    static readonly (string key, Color color)[] Patches =
    {
        ("calib.patch.white", Color.FromRgb(255, 255, 255)),
        ("calib.patch.grey", Color.FromRgb(128, 128, 128)),
        ("calib.patch.red", Color.FromRgb(255, 0, 0)),
        ("calib.patch.green", Color.FromRgb(0, 255, 0)),
        ("calib.patch.blue", Color.FromRgb(0, 0, 255)),
        ("calib.patch.yellow", Color.FromRgb(255, 255, 0)),
        ("calib.patch.orange", Color.FromRgb(255, 128, 0)),
        ("calib.patch.cyan", Color.FromRgb(0, 255, 255)),
        ("calib.patch.sky", Color.FromRgb(0, 128, 255)),
        ("calib.patch.magenta", Color.FromRgb(255, 0, 255))
    };

    PatchWindow? _patchWindow;
    int _patchIndex;

    /// <summary>LED brightness under a test colour; session state, like the colour itself.</summary>
    double _patchBrightness = 0.5;

    /// <summary>The painting was started for the test colour and stops with it.</summary>
    bool _patchStartedPainting;

    Button? _patchButton;
    TextBlock? _patchText;

    /// <summary>Settings switched off by another one; the same dimming as <see cref="Dimmed"/>.</summary>
    const double DisabledOpacity = 0.45;

    void BuildCalibrationSection() => AddSection(Loc.T("tab.calibration"), "", panel =>
    {
        panel.Children.Add(Ui.Note(Loc.T("calib.head")));

        _patchButton = Ui.Btn("", TogglePatch);
        var patchRow = Ui.Row(_patchButton, Ui.HelpIcon(Loc.T("calib.patches.note")));
        panel.Children.Add(patchRow);

        // стрелки стоят перед названием цвета и не сдвигаются при его смене
        Button Arrow(string glyph, int step) => WithGlyph(Ui.Btn("", () => StepPatch(step)), glyph);

        _patchText = new TextBlock
        {
            Foreground = Ui.Fg,
            FontSize = Ui.TextSize,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(Ui.Row(Arrow("", -1), Arrow("", 1), _patchText));

        panel.Children.Add(Ui.Slider(Loc.T("calib.patches.brightness"), _patchBrightness, 0.1, 1, 0.05,
            v => { _patchBrightness = v; PushColourPatch(); }, "",
            Loc.T("calib.patches.brightness.note"),
            format: v => (v * 100).ToString("0", CultureInfo.InvariantCulture) + " %"));

        UpdatePatchControls();

        panel.Children.Add(Ui.Header(Loc.T("calib.scene"), Loc.T("calib.scene.note")));
        BuildCalibration(panel, _scene, editable: true, Touch, () =>
        {
            // баланс белого в разделе «Цвет» и вкладка фигуры показывают, заменён ли он
            RebuildSections();
            BuildFixturePanel();
        });
    });

    static Button WithGlyph(Button b, string glyph)
    {
        b.Content = new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 12 };
        b.Padding = new Thickness(10, 8, 10, 8);
        return b;
    }

    /// <summary>
    /// The switch and the sliders of one calibration.
    ///
    /// Hue and saturation run on a perceptual scale rather than by the share of the channel
    /// mixed in: the share is linear light, and the eye sees it roughly to the power of
    /// 1/2.2. On a linear scale saturation between 0.95 and 1 changed the colour more than
    /// the rest of the travel. The slider holds the position, the setting gets the share.
    /// </summary>
    /// <param name="c">What is shown, and written when <paramref name="editable"/>.</param>
    /// <param name="changed">Marks the edit; called after every value.</param>
    /// <param name="rebuild">Builds the page again once the switch has moved.</param>
    void BuildCalibration(StackPanel p, ICalibrated c, bool editable, Action changed, Action rebuild)
    {
        p.Children.Add(Ui.Check(Loc.T("calib.enable"), c.Calibration, v =>
        {
            if (_rebuildingUi) return;

            // первое включение начинает с белого, уже подобранного в разделе «Цвет»
            if (v) ColourCalibration.SeedWhite(c);
            c.Calibration = v;

            // пересборка ставит _rebuildingUi, и отметка после неё не сработала бы
            changed();
            rebuild();
        }, Loc.T("calib.enable.note"), enabled: editable));

        bool live = editable && c.Calibration;
        var block = new StackPanel { Opacity = !editable || c.Calibration ? 1 : DisabledOpacity };
        p.Children.Add(block);

        block.Children.Add(Ui.Header(Loc.T("calib.white"), Loc.T("calib.white.note")));
        block.Children.Add(Ui.Slider(Loc.T("calib.white.r"), c.CalWhiteR, 0.2, 1, 0.005,
            v => { c.CalWhiteR = v; changed(); }, "", format: Three, enabled: live));
        block.Children.Add(Ui.Slider(Loc.T("calib.white.g"), c.CalWhiteG, 0.2, 1, 0.005,
            v => { c.CalWhiteG = v; changed(); }, "", format: Three, enabled: live));
        block.Children.Add(Ui.Slider(Loc.T("calib.white.b"), c.CalWhiteB, 0.2, 1, 0.005,
            v => { c.CalWhiteB = v; changed(); }, "", format: Three, enabled: live));

        const double Curve = 2.2, HueMax = 0.5;
        static double HueFromPos(double v) => Math.Sign(v) * HueMax * Math.Pow(Math.Abs(v), Curve);
        static double PosFromHue(double h) => Math.Sign(h) * Math.Pow(Math.Min(1, Math.Abs(h) / HueMax), 1 / Curve);
        static double SatFromPos(double v) => 1 - Math.Pow(1 - v, Curve);
        static double PosFromSat(double k) => 1 - Math.Pow(Math.Clamp(1 - k, 0, 1), 1 / Curve);

        void Primary(string name, string plus, string minus, double hue, Action<double> setHue,
                     double sat, Action<double> setSat)
        {
            block.Children.Add(Ui.Header(Loc.T(name)));
            block.Children.Add(Ui.Slider(Loc.T("calib.hue"), PosFromHue(hue), -1, 1, 0.01,
                v => { setHue(HueFromPos(v)); changed(); }, "", Loc.T("calib.hue.note"),
                format: v => Math.Abs(v) < 0.005 ? "0"
                    : string.Format(Loc.T(v > 0 ? plus : minus), Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture)),
                enabled: live));
            block.Children.Add(Ui.Slider(Loc.T("calib.sat"), PosFromSat(sat), 0, 1, 0.01,
                v => { setSat(SatFromPos(v)); changed(); }, "", Loc.T("calib.sat.note"),
                format: v => v.ToString("0.00", CultureInfo.InvariantCulture), enabled: live));
        }

        Primary("calib.patch.red", "calib.toYellow", "calib.toMagenta",
            c.CalHueR, v => c.CalHueR = v, c.CalSatR, v => c.CalSatR = v);
        Primary("calib.patch.green", "calib.toCyan", "calib.toYellow",
            c.CalHueG, v => c.CalHueG = v, c.CalSatG, v => c.CalSatG = v);
        Primary("calib.patch.blue", "calib.toMagenta", "calib.toCyan",
            c.CalHueB, v => c.CalHueB = v, c.CalSatB, v => c.CalSatB = v);
    }

    static string Three(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The temperature and the gains, switched off while the calibration replaces them.
    /// Dimmed as a block, since a disabled Fluent slider looks nearly like a live one.
    /// </summary>
    void BuildBalance(StackPanel p, ICalibrated c, bool editable, Action changed)
    {
        if (c.Calibration) p.Children.Add(Ui.Note(Loc.T("color.calibrated")));

        bool live = editable && !c.Calibration;
        var block = new StackPanel { Opacity = !editable || !c.Calibration ? 1 : DisabledOpacity };
        p.Children.Add(block);

        block.Children.Add(Ui.Slider(Loc.T("color.temperature"), c.TemperatureK, 1500, 15000, 100,
            v => { c.TemperatureK = (int)v; changed(); }, " K", enabled: live));

        block.Children.Add(Ui.Header(Loc.T("color.gains"), Loc.T("color.gains.note")));
        block.Children.Add(Ui.Slider(Loc.T("color.red"), c.GainR, 0, 2, 0.01,
            v => { c.GainR = v; changed(); }, enabled: live));
        block.Children.Add(Ui.Slider(Loc.T("color.green"), c.GainG, 0, 2, 0.01,
            v => { c.GainG = v; changed(); }, enabled: live));
        block.Children.Add(Ui.Slider(Loc.T("color.blue"), c.GainB, 0, 2, 0.01,
            v => { c.GainB = v; changed(); }, enabled: live));
    }

    // ---- тестовые цвета ---------------------------------------------------

    void TogglePatch()
    {
        if (_patchWindow != null) HidePatch();
        else ShowPatch();
    }

    void StepPatch(int step)
    {
        _patchIndex = (_patchIndex + step + Patches.Length) % Patches.Length;
        _patchWindow?.SetColor(Patches[_patchIndex].color);
        PushColourPatch();
        UpdatePatchControls();
    }

    /// <summary>
    /// Fills the screen of the layout with the test colour, under this window, and puts the
    /// same colour on every fixture. The painting is started for it if it was not running,
    /// and the placement test is stopped: both drive the painter instead of the screen.
    /// </summary>
    void ShowPatch()
    {
        var monitor = ScreenChoice.Find(_scene.MonitorDeviceName, _scene.MonitorModel);
        if (monitor == null) { Say(Loc.P("Экран не найден.", "No screen found.")); return; }

        if (_painter.TestActive || _view.TestMode) StopTest();

        if (!_painter.IsRunning)
        {
            EnsureServer();
            _hub.Connect();
            _painter.UseScene(_scene);
            _painter.Start();
            _patchStartedPainting = true;
        }

        PushColourPatch(force: true);

        _patchWindow = new PatchWindow(this, monitor, Patches[_patchIndex].color);
        _patchWindow.Show();
        _patchWindow.PlaceBelowOwner();

        UpdatePatchControls();
        UpdateStartButton();
        Say(Loc.P("Тестовый цвет на экране и на всех фигурах. Esc убирает его.",
                  "Test colour on the screen and on every fixture. Esc removes it."));
    }

    void HidePatch()
    {
        if (_patchWindow == null) return;

        var window = _patchWindow;
        _patchWindow = null;
        window.Close();

        _painter.SetColourPatch(null);
        if (_patchStartedPainting && !_paintingWanted) _painter.Stop();
        _patchStartedPainting = false;

        UpdatePatchControls();
        UpdateStartButton();
    }

    /// <summary>Hands the painter the test colour as it stands right now.</summary>
    void PushColourPatch(bool force = false)
    {
        if (_patchWindow == null && !force) return;

        var c = Patches[_patchIndex].color;
        _painter.SetColourPatch(new ColourPatch { R = c.R, G = c.G, B = c.B, Dim = _patchBrightness });
    }

    void UpdatePatchControls()
    {
        if (_patchButton != null)
            _patchButton.Content = Loc.T(_patchWindow != null ? "calib.patches.hide" : "calib.patches.show");

        if (_patchText != null)
        {
            var (key, c) = Patches[_patchIndex];
            _patchText.Text = string.Format(Loc.T("calib.patch.value"), Loc.T(key), c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Esc removes the test colour. The colour window never takes the keyboard, so the key
    /// arrives here whichever control has the focus.
    /// </summary>
    void HookPatchKeys()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _patchWindow == null) return;

            HidePatch();
            e.Handled = true;
        };

        // свёрнутое или убранное в трей окно оставило бы экран залитым без способа это снять
        IsVisibleChanged += (_, _) => { if (!IsVisible) HidePatch(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HidePatch(); };
    }
}
