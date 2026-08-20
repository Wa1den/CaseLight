using System;
using System.Threading;
using System.Windows;

namespace CaseLight;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Two copies would fight over the same LEDs and, worse, both write scene.json on
        // exit - the one closed last would silently overwrite the other's layout.
        using var single = new Mutex(true, @"Local\CaseLightSingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("CaseLight уже запущен.", "CaseLight",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var app = new Application();
        app.Run(new MainWindow());
    }
}
