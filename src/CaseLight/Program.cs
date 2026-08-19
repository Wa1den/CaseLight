using System;
using System.Windows;

namespace CaseLight;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        app.Run(new MainWindow());
    }
}
