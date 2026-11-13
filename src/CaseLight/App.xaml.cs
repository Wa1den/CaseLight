using System;
using System.Threading;
using System.Windows;

using CaseLight.Core.Capture;
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

    /// <summary>
    /// Writes an exception that is about to end the process into the program's own log.
    ///
    /// Without it such a crash left nothing in caselight.log: on 13.11.2026 the pen thread
    /// failed on a runtime removed by an update, and the stack was only found in the Windows
    /// event log.
    /// </summary>
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ProbeLog.Log(Loc.P("сбой", "crash"),
                Loc.P("необработанное исключение, программа завершается: ",
                      "unhandled exception, the program is terminating: ") + e.ExceptionObject);
    }

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

    /// <summary>
    /// Lets the next copy start while this one is still on its way out. Only for the restart
    /// onto a new runtime: by then this copy has saved its settings and let go of the
    /// devices, and without it the new copy would only say that CaseLight is already running.
    /// </summary>
    public void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); }
        catch (ApplicationException) { /* не наш - значит, и держать нечего */ }
        _single?.Dispose();
        _single = null;
    }
}
