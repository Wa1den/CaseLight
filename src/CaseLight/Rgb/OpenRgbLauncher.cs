using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Ambilight.Capture;

namespace CaseLight.Rgb;

/// <summary>
/// Starts the OpenRGB server when it is not already running.
///
/// Without the DRAM modules there is nothing left that needs the SMBus, and the SMBus is
/// the only reason OpenRGB ever needed administrator rights - the motherboard controller
/// and the graphics card are plain USB and I2C, reachable from a normal user session. So
/// this starts it unelevated, which also means no UAC prompt on every login.
/// </summary>
public static class OpenRgbLauncher
{
    /// <summary>Detection takes several seconds before the port opens; this is the ceiling.</summary>
    public const int TypicalStartupMs = 20000;

    public static bool IsRunning() =>
        Process.GetProcessesByName("OpenRGB").Length > 0;

    /// <summary>Usual install locations first, then whatever the uninstall entry recorded.</summary>
    public static string? FindExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe")
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var path in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                try
                {
                    using var key = root.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub?.GetValue("DisplayName") is not string display || !display.Contains("OpenRGB")) continue;

                        if (sub.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                        {
                            string exe = Path.Combine(location, "OpenRGB.exe");
                            if (File.Exists(exe)) return exe;
                        }

                        // some installers only record the uninstaller, but it sits beside the exe
                        if (sub.GetValue("UninstallString") is string uninstall)
                        {
                            string? dir = Path.GetDirectoryName(uninstall.Trim('"'));
                            if (dir != null)
                            {
                                string exe = Path.Combine(dir, "OpenRGB.exe");
                                if (File.Exists(exe)) return exe;
                            }
                        }
                    }
                }
                catch { /* ветка может быть недоступна - просто пробуем следующую */ }
            }

        return null;
    }

    /// <summary>
    /// Closes the server: politely first, by force if it will not go.
    ///
    /// Politely matters - OpenRGB writes its zone sizes on exit, and killing it outright
    /// eventually loses the strip lengths that were measured by hand.
    /// </summary>
    public static string Stop(int graceMs = 4000)
    {
        var processes = Process.GetProcessesByName("OpenRGB");
        if (processes.Length == 0) return "OpenRGB не запущен";

        foreach (var p in processes)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                if (!p.WaitForExit(graceMs)) p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                ProbeLog.Log("OpenRGB", "не удалось закрыть: " + ex.Message);
            }
            finally { p.Dispose(); }
        }

        ProbeLog.Log("OpenRGB", "остановлен");
        return "OpenRGB остановлен";
    }

    /// <summary>
    /// Full restart. Blunt, but it is the only thing that reliably brings the lighting back
    /// after sleep: the controllers are re-enumerated while the machine is out, and the
    /// running server keeps writing into handles that no longer lead anywhere - which is
    /// why it reports success while the case sits in its power-on pattern.
    /// </summary>
    public static string Restart(string? exePath, bool asAdmin)
    {
        Stop();

        // let Windows finish releasing the USB handles before the new instance claims them
        System.Threading.Thread.Sleep(1500);

        return Launch(exePath, asAdmin);
    }

    /// <returns>What happened, ready for the status line.</returns>
    public static string Launch(string? exePath, bool asAdmin)
    {
        if (IsRunning()) return "OpenRGB уже запущен";

        exePath ??= FindExe();
        if (exePath == null || !File.Exists(exePath))
            return "не нашёл OpenRGB.exe — укажи путь вручную";

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,

                // --server открывает порт 6742, --startminimized не бросает окно в лицо
                Arguments = "--server --startminimized",
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true
            };

            if (asAdmin) info.Verb = "runas";

            Process.Start(info);
            ProbeLog.Log("OpenRGB", "запущен: " + exePath);
            return "OpenRGB запущен, идёт поиск устройств";
        }
        catch (Exception ex)
        {
            ProbeLog.Log("OpenRGB", "не удалось запустить: " + ex.Message);
            return "не удалось запустить OpenRGB: " + ex.Message;
        }
    }
}
