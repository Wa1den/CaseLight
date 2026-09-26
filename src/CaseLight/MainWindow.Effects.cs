using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CaseLight.Core.Capture;
using CaseLight.Plugins;
using CaseLight.Rgb;

using CaseLight.Core.Text;

namespace CaseLight;

/// <summary>
/// The sections of effects: one per running <see cref="ILightEffect"/>, built from the
/// settings it declares.
///
/// The page is built here rather than by the plugin so that it looks like every other page
/// and keeps its values in the settings file, under Apply and Cancel like the rest.
/// </summary>
public sealed partial class MainWindow
{
    void BuildEffectSections()
    {
        foreach (var (key, effect) in _plugins.Effects())
        {
            string name, icon;
            IReadOnlyList<EffectSetting> settings;
            EffectTarget target;

            try
            {
                name = effect.Name;
                icon = effect.Icon;
                settings = effect.Settings;
                target = effect.Target;
            }
            catch (Exception ex)
            {
                ProbeLog.Log(Loc.P("эффекты", "effects"), key + ": " + ex.Message);
                continue;
            }

            var entry = _plugins.Entries.FirstOrDefault(e => e.Effects.Contains(effect));
            string missing = entry == null ? "" : _plugins.Missing(entry);

            AddSection(name, icon, panel =>
            {
                if (missing != "") panel.Children.Add(Ui.Warning(missing));
                BuildEffectPage(panel, key, settings, target);
            });
        }
    }

    void BuildEffectPage(StackPanel panel, string key, IReadOnlyList<EffectSetting> settings, EffectTarget target)
    {
        var stored = _scene.Effects.TryGetValue(key, out var v) ? v : new Dictionary<string, string>();
        var values = new EffectValues(stored, settings);

        void Set(string setting, string value)
        {
            if (_rebuildingUi) return;

            // Замена целиком, а не правка: копия за «Отменой» делит с живой те же словари.
            var inner = _scene.Effects.TryGetValue(key, out var old)
                ? new Dictionary<string, string>(old) : new Dictionary<string, string>();
            inner[setting] = value;

            _scene.Effects = new Dictionary<string, Dictionary<string, string>>(_scene.Effects) { [key] = inner };
            Touch();
        }

        if (target == EffectTarget.Fixtures)
        {
            panel.Children.Add(BuildFixtureChoice(values, Set));

            foreach (var s in settings)
            {
                try { panel.Children.Add(BuildEffectSetting(s, values, [], 0, Set)); }
                catch (Exception ex) { ProbeLog.Log(Loc.P("эффекты", "effects"), key + "." + s.Key + ": " + ex.Message); }
            }
            return;
        }

        // ---- устройство
        var devices = _plugins.Devices().Select(d => d.Device).ToArray();
        var names = devices.Select(DeviceName).Where(n => n != "").Distinct().ToList();

        string chosen = values.Device;
        var box = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        box.Items.Add(Loc.T("effects.nodevice"));
        foreach (var n in names) box.Items.Add(n);

        // выбранное устройство показывается и отключённым, иначе выбор молча сбросился бы
        if (chosen != "" && !names.Contains(chosen))
        {
            names.Add(chosen);
            box.Items.Add(string.Format(Loc.T("effects.absent"), chosen));
        }

        box.SelectedIndex = chosen == "" ? 0 : names.IndexOf(chosen) + 1;
        box.SelectionChanged += (_, _) =>
        {
            if (_rebuildingUi || box.SelectedIndex < 0) return;

            string picked = box.SelectedIndex == 0 ? "" : names[box.SelectedIndex - 1];
            if (picked == chosen) return;

            Set(EffectValues.DeviceKey, picked);

            // ряды и число диодов у другого устройства другие
            RebuildSections();
        };
        panel.Children.Add(Ui.Labeled(Loc.T("effects.device"), box, Loc.T("effects.device.note")));

        var device = devices.FirstOrDefault(d => DeviceName(d) == chosen);
        var geometry = device == null ? ((IReadOnlyList<LedRect>?)null, Array.Empty<int[]>(), 0) : EffectMixer.Geometry(device);

        foreach (var s in settings)
        {
            try { panel.Children.Add(BuildEffectSetting(s, values, geometry.Item2, geometry.Item3, Set)); }
            catch (Exception ex) { ProbeLog.Log(Loc.P("эффекты", "effects"), key + "." + s.Key + ": " + ex.Message); }
        }
    }

    UIElement BuildEffectSetting(EffectSetting s, EffectValues values, int[][] rows, int ledCount,
                                 Action<string, string> set)
    {
        switch (s.Kind)
        {
            case SettingKind.Header:
                return Ui.Header(s.Label, s.Help);

            case SettingKind.Toggle:
                return Ui.Check(s.Label, values.Bool(s.Key), on => set(s.Key, on ? "1" : "0"), s.Help);

            case SettingKind.Slider:
                return Ui.Slider(s.Label, values.Number(s.Key), s.Min, s.Max, s.Step,
                    x => set(s.Key, x.ToString("0.###", CultureInfo.InvariantCulture)), s.Unit, s.Help);

            case SettingKind.Choice:
            {
                var box = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
                foreach (var o in s.Options) box.Items.Add(o);
                box.SelectedIndex = Math.Clamp(values.Int(s.Key), 0, Math.Max(0, s.Options.Count - 1));
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedIndex >= 0) set(s.Key, box.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                };
                return Ui.Labeled(s.Label, box, s.Help);
            }

            case SettingKind.Color:
            {
                var colour = values.Color(s.Key);
                var swatch = new Border
                {
                    Width = 28,
                    Height = 22,
                    CornerRadius = new CornerRadius(3),
                    BorderBrush = Ui.PanelStroke,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B))
                };

                var row = Ui.Row(swatch, Ui.Btn(Loc.T("effects.pick"), () =>
                {
                    if (PickColour(ref colour))
                    {
                        swatch.Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
                        set(s.Key, colour.ToString());
                    }
                }));
                row.Margin = new Thickness(0, 2, 0, 4);
                return Ui.Labeled(s.Label, row, s.Help);
            }

            case SettingKind.Rows:
                return BuildRowsSetting(s, values, rows, set);

            case SettingKind.Led:
            {
                int led = values.Int(s.Key);
                var panel = new StackPanel();
                panel.Children.Add(Ui.IntBox(s.Label, led + 1, n =>
                {
                    if (n >= 1 && (ledCount == 0 || n <= ledCount))
                        set(s.Key, (n - 1).ToString(CultureInfo.InvariantCulture));
                }, s.Help));
                if (ledCount > 0) panel.Children.Add(Ui.Note(string.Format(Loc.T("effects.ledrange"), ledCount)));
                return panel;
            }

            default:
                return new TextBlock();
        }
    }

    /// <summary>
    /// A checkbox per fixture of the plan, for an effect drawn on fixtures. Switched-off
    /// fixtures are listed too: the choice is kept, and the effect shows once the fixture is
    /// on again.
    /// </summary>
    UIElement BuildFixtureChoice(EffectValues values, Action<string, string> set)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(Ui.Caption(Loc.T("effects.fixtures"), Loc.T("effects.fixtures.note")));

        Model.Fixture[] fixtures;
        lock (_scene.Fixtures) fixtures = _scene.Fixtures.ToArray();

        if (fixtures.Length == 0)
        {
            panel.Children.Add(Ui.Note(Loc.T("effects.nofixtures")));
            return panel;
        }

        // порядок выбора не важен, а фигура, удалённая с плана, из списка уходит при первой правке
        var picked = new HashSet<string>(values.Fixtures);

        foreach (var f in fixtures)
        {
            var fixture = f;
            string label = fixture.Enabled ? fixture.Name : string.Format(Loc.T("effects.fixtureoff"), fixture.Name);

            panel.Children.Add(Ui.Check(label, picked.Contains(fixture.Id), on =>
            {
                if (on) picked.Add(fixture.Id);
                else picked.Remove(fixture.Id);

                var present = fixtures.Select(x => x.Id).Where(picked.Contains);
                set(EffectValues.FixturesKey, string.Join(",", present));
            }));
        }

        return panel;
    }

    /// <summary>A checkbox per row of the device, numbered from the top.</summary>
    UIElement BuildRowsSetting(EffectSetting s, EffectValues values, int[][] rows, Action<string, string> set)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(Ui.Caption(s.Label, s.Help));

        if (rows.Length == 0)
        {
            panel.Children.Add(Ui.Note(Loc.T("effects.norows")));
            return panel;
        }

        var picked = new SortedSet<int>(values.Ints(s.Key));
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };

        for (int r = 0; r < rows.Length; r++)
        {
            int row = r;
            var check = new CheckBox
            {
                Content = string.Format(Loc.T("effects.row"), row + 1, rows[row].Length),
                IsChecked = picked.Contains(row),
                FontSize = Ui.TextSize,
                MinWidth = 0,
                Margin = new Thickness(0, 3, 16, 3)
            };

            void Changed(bool on)
            {
                if (on) picked.Add(row);
                else picked.Remove(row);
                set(s.Key, string.Join(",", picked.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            }

            check.Checked += (_, _) => Changed(true);
            check.Unchecked += (_, _) => Changed(false);
            wrap.Children.Add(check);
        }

        panel.Children.Add(wrap);
        return panel;
    }

    static bool PickColour(ref LightColor colour)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B),
            FullOpen = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

        colour = new LightColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        return true;
    }

    static string DeviceName(ILightDevice device)
    {
        try { return device.Name; }
        catch { return ""; }
    }
}
