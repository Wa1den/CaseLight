using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CaseLight;

/// <summary>
/// The small controls the settings tabs are built from.
///
/// Kept in one place so every tab looks the same without a stylesheet: the window is
/// assembled in code, and repeating the same margins by hand is how panels start drifting
/// apart.
/// </summary>
public static class Ui
{
    public static readonly Brush Fg = Brushes.White;
    public static readonly Brush FgDim = new SolidColorBrush(Color.FromRgb(150, 158, 172));
    public static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(38, 41, 48));
    public static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(30, 32, 38));
    public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(230, 180, 90));

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        Foreground = Fg,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 14, 0, 6)
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = FgDim,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8),
        FontSize = 11
    };

    public static TextBlock Mono(string text = "") => new()
    {
        Text = text,
        Foreground = Fg,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    public static UIElement Labelled(string label, UIElement editor)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = FgDim, FontSize = 11 });
        panel.Children.Add(editor);
        return panel;
    }

    public static UIElement Text(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => set(box.Text);
        return Labelled(label, box);
    }

    /// <summary>Accepts both a comma and a dot, because both get typed.</summary>
    public static UIElement Num(string label, double value, Action<double> set)
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
        return Labelled(label, box);
    }

    public static UIElement Int(string label, int value, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 2, 0, 0) };
        box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out int v)) set(v); };
        return Labelled(label, box);
    }

    public static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = FgDim,
            Margin = new Thickness(0, 5, 0, 3)
        };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    /// <summary>A slider that shows its own value - otherwise it is a guess with a handle.</summary>
    public static UIElement Slide(string label, double value, double min, double max,
                                  double step, Action<double> set, string suffix = "")
    {
        var caption = new TextBlock { Foreground = FgDim, FontSize = 11 };
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
        panel.Children.Add(caption);
        panel.Children.Add(slider);
        return panel;
    }

    public static Button Btn(string caption, Action onClick)
    {
        var b = new Button { Content = caption, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A caption followed by a clickable address.</summary>
    public static TextBlock Link(string caption, string url)
    {
        var t = new TextBlock
        {
            Foreground = FgDim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        if (!string.IsNullOrEmpty(caption)) t.Inlines.Add(caption + " ");

        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run(url)) { Foreground = Fg };
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
