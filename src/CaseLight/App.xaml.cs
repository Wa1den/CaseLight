using System.Threading;
using System.Windows;

using CaseLight.Core.Text;

namespace CaseLight;

/// <summary>
/// The entry point, in XAML so that <c>ThemeMode</c> and the theme-derived resources are
/// declared in one place before any window exists.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Kept in a field for the whole run rather than disposed at the end of a method.
    ///
    /// Two copies would fight over the same LEDs and, worse, both write scene.json on exit:
    /// the one closed last would silently overwrite the other's layout.
    /// </summary>
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\CaseLightSingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show(Loc.P("CaseLight уже запущен.", "CaseLight is already running."), "CaseLight",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // The window is built in code, so there is no StartupUri to point at.
        new MainWindow().Show();
    }
}
