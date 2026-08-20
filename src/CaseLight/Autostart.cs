using System;
using Microsoft.Win32;

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
            if (key == null) return "не удалось открыть ветку автозапуска";

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExePath)) return "не удалось определить путь к программе";
                key.SetValue(ValueName, "\"" + ExePath + "\"");

                // Worth spelling out: CaseLight needs the OpenRGB server, and that one has
                // to run as administrator to reach the SMBus at all. Starting with Windows
                // only helps if OpenRGB is arranged to start too.
                return "автозапуск включён: " + ExePath;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return "автозапуск выключен";
        }
        catch (Exception ex)
        {
            return "не удалось изменить автозапуск: " + ex.Message;
        }
    }
}
