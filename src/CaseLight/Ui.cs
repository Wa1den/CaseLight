using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CaseLight;

/// <summary>
/// The small controls the settings pages are built from.
///
/// Kept in one place so every page looks the same without a stylesheet: the window is
/// assembled in code, and repeating the same margins by hand is how panels start drifting
/// apart.
///
/// Nothing here carries a colour of its own. Every brush is an alias declared in App.xaml
/// over a Fluent theme token, so the whole window follows the system light/dark switch
/// without a single value to keep in sync.
/// </summary>
public static class Ui
{
    /// <summary>Body text and captions share one size, so a page reads as one block.</summary>
    public const double TextSize = 14;

    /// <summary>
    /// Application resources rather than the window's own.
    ///
    /// A window dictionary is searched first and does not contain these keys; the indexer
    /// would return null and the text would fall back to system black on a dark panel.
    /// </summary>
    static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush Fg => Res("Fg");
    public static Brush FgDim => Res("FgDim");
    public static Brush Panel => Res("Panel");
    public static Brush PanelStroke => Res("PanelStroke");
    public static Brush Warn => Res("Warn");

    public static FontFamily IconFont =>
        Application.Current?.TryFindResource("Icons") as FontFamily ?? new FontFamily("Segoe UI");

    /// <summary>A panel with the card look: filled, outlined, rounded, padded.</summary>
    public static Border Card(UIElement child)
    {
        var border = new Border { Child = child };
        if (Application.Current?.TryFindResource("Card") is Style style) border.Style = style;
        return border;
    }

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Foreground = Fg,
        FontWeight = FontWeights.SemiBold,
        FontSize = TextSize,
        Margin = new Thickness(0, 16, 0, 6)
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = FgDim,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8),
        FontSize = TextSize
    };

    /// <summary>Fixed-width figures, so the statistics block does not shift as values change.</summary>
    public static TextBlock Mono(string text = "") => new()
    {
        Text = text,
        Foreground = Fg,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>
    /// The glyph that carries an explanation in its tooltip.
    ///
    /// Explanations used to sit under their setting as permanent grey paragraphs, which
    /// made a page of eight settings mostly prose. On the glyph they are one hover away and
    /// take no room until asked for.
    /// </summary>
    public static TextBlock Help(string text)
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = 12,
            Foreground = FgDim,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,

            // the popup inherits the font of the element it belongs to, and the icon font
            // has no letters at all - the text has to name its own
            ToolTip = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            }
        };

        ToolTipService.SetInitialShowDelay(icon, 200);
        ToolTipService.SetShowDuration(icon, 60000);
        return icon;
    }

    /// <summary>A caption, optionally with its explanation glyph, above the editor.</summary>
    public static UIElement Labelled(string label, UIElement editor, string? help = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var caption = new TextBlock { Text = label, Foreground = FgDim, FontSize = TextSize };

        if (help == null) panel.Children.Add(caption);
        else panel.Children.Add(WithHelp(caption, help));

        panel.Children.Add(editor);
        return panel;
    }

    static StackPanel WithHelp(UIElement element, string help)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(element);
        row.Children.Add(Help(help));
        return row;
    }

    public static UIElement Text(string label, string value, Action<string> set, string? help = null)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => set(box.Text);
        return Labelled(label, box, help);
    }

    /// <summary>Accepts both a comma and a dot, because both get typed.</summary>
    public static UIElement Num(string label, double value, Action<double> set, string? help = null)
    {
        var box = new TextBox
        {
            Text = value.ToString("0.###", CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 2, 0, 0)
        };
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double v))
                set(v);
        };
        return Labelled(label, box, help);
    }

    public static UIElement Int(string label, int value, Action<int> set, string? help = null)
    {
        var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out int v)) set(v); };
        return Labelled(label, box, help);
    }

    public static UIElement Check(string label, bool value, Action<bool> set, string? help = null)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = Fg,
            FontSize = TextSize,
            Margin = new Thickness(0, 5, 0, 3),

            // the Fluent style reserves 120px of width; with a short label the help glyph
            // would sit far to the right of the text it explains
            MinWidth = 0
        };

        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);

        return help == null ? box : WithHelp(box, help);
    }

    /// <summary>A slider that shows its own value - otherwise it is a guess with a handle.</summary>
    public static UIElement Slide(string label, double value, double min, double max,
                                  double step, Action<double> set, string suffix = "",
                                  string? help = null)
    {
        var caption = new TextBlock { Foreground = FgDim, FontSize = TextSize };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            Margin = new Thickness(0, 2, 0, 0)
        };

        void Show() => caption.Text = $"{label}: {slider.Value.ToString("0.##", CultureInfo.InvariantCulture)}{suffix}";
        Show();

        slider.ValueChanged += (_, _) => { Show(); set(slider.Value); };

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(help == null ? caption : WithHelp(caption, help));
        panel.Children.Add(slider);
        return panel;
    }

    public static Button Btn(string caption, Action onClick, bool accent = false)
    {
        var b = new Button
        {
            Content = caption,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 0)
        };

        // the accent style ships with the Fluent theme and is absent without it
        if (accent && Application.Current?.TryFindResource("AccentButtonStyle") is Style style)
            b.Style = style;

        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A caption followed by a clickable address.</summary>
    public static TextBlock Link(string caption, string url)
    {
        var t = new TextBlock
        {
            Foreground = FgDim,
            FontSize = TextSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        if (!string.IsNullOrEmpty(caption)) t.Inlines.Add(caption + " ");

        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(url));

        // The stock hyperlink is web-blue with a red hover, both hardcoded in its default
        // style and neither readable on the dark theme. A local foreground wins over the
        // style triggers, so the link stays in the theme's accent colour.
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                                  "AccentTextFillColorPrimaryBrush");

        link.Click += (_, _) => OpenUrl(url);
        t.Inlines.Add(link);

        return t;
    }

    static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute hands it to the default browser; without it .NET tries to
            // execute the address as a program
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CaseLight.Core.Capture.ProbeLog.Log("ссылка", "не удалось открыть " + url + ": " + ex.Message);
        }
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var i in items) panel.Children.Add(i);
        return panel;
    }
}
