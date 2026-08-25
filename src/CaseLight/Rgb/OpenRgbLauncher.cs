using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using CaseLight.Core.Capture;

using CaseLight.Core.Text;

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
        if (processes.Length == 0) return Loc.P("OpenRGB не запущен", "OpenRGB is not running");

        // A server the task started runs elevated, and nothing below it can close it: the
        // window message is dropped and Kill answers "access denied". The scheduler can,
        // so it gets asked first and the manual path is left for a server started by hand.
        if (OpenRgbTask.Exists() && OpenRgbTask.TryStop())
        {
            for (int i = 0; i < 25 && IsRunning(); i++) System.Threading.Thread.Sleep(200);

            if (!IsRunning())
            {
                ProbeLog.Log("OpenRGB", Loc.P("остановлен заданием планировщика", "stopped by the scheduled task"));
                return Loc.P("OpenRGB остановлен", "OpenRGB stopped");
            }
        }

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
                ProbeLog.Log("OpenRGB", Loc.P("не удалось закрыть: ", "could not close it: ") + ex.Message);
            }
            finally { p.Dispose(); }
        }

        ProbeLog.Log("OpenRGB", Loc.P("остановлен", "stopped"));
        return Loc.P("OpenRGB остановлен", "OpenRGB stopped");
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
        if (IsRunning()) return Loc.P("OpenRGB уже запущен", "OpenRGB is already running");

        // The scheduled task, when there is one, starts the server with the rights the
        // SMBus needs and without a prompt. Starting it by hand here would either lose
        // those rights or ask for them again.
        if (OpenRgbTask.Exists() && OpenRgbTask.TryStart())
            return Loc.P("OpenRGB запущен заданием планировщика, идёт поиск устройств", "OpenRGB started by the scheduled task, looking for devices");

        exePath ??= FindExe();
        if (exePath == null || !File.Exists(exePath))
            return Loc.P("OpenRGB.exe не найден, укажите путь вручную", "OpenRGB.exe not found, set the path by hand");

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
            ProbeLog.Log("OpenRGB", Loc.P("запущен: ", "started: ") + exePath);
            return Loc.P("OpenRGB запущен, идёт поиск устройств", "OpenRGB started, looking for devices");
        }
        catch (Exception ex)
        {
            ProbeLog.Log("OpenRGB", Loc.P("не удалось запустить: ", "could not start it: ") + ex.Message);
            return Loc.P("не удалось запустить OpenRGB: ", "could not start OpenRGB: ") + ex.Message;
        }
    }
}
