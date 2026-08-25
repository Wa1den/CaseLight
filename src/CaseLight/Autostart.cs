using System;
using Microsoft.Win32;

using CaseLight.Core.Text;

namespace CaseLight;

/// <summary>
/// Per-user autostart through the standard Run key. User scope only - no elevation, and
/// nothing outside this account is touched.
/// </summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "CaseLight";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string s && s.Contains("CaseLight");
        }
        catch
        {
            return false;
        }
    }

    /// <returns>What happened, ready to show in the status line.</returns>
    public static string Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key == null) return Loc.P("не удалось открыть ветку автозапуска", "could not open the autostart key");

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExePath)) return Loc.P("не удалось определить путь к программе", "could not work out the path to the program");
                key.SetValue(ValueName, "\"" + ExePath + "\"");

                // Worth spelling out: CaseLight needs the OpenRGB server, and that one has
                // to run as administrator to reach the SMBus at all. Starting with Windows
                // only helps if OpenRGB is arranged to start too.
                return Loc.P("автозапуск включён: ", "autostart on: ") + ExePath;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Loc.P("автозапуск выключен", "autostart off");
        }
        catch (Exception ex)
        {
            return Loc.P("не удалось изменить автозапуск: ", "could not change the autostart: ") + ex.Message;
        }
    }
}
