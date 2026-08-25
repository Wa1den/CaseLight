using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CaseLight.Core.Capture;

using CaseLight.Core.Text;

namespace CaseLight.Rgb;

/// <summary>
/// Starts the OpenRGB server with administrator rights at logon, through a scheduled task.
///
/// The SMBus is the only bus that needs elevation, and it is the one the memory modules sit
/// on. Without it OpenRGB does not even detect them, so after a cold boot the modules keep
/// whatever their own controller does by default - which is the rainbow every board ships
/// with - and nothing on this side can overwrite it.
///
/// The Run key cannot elevate, and elevating by hand means a UAC prompt at every logon. A
/// scheduled task registered to run with the highest available rights is the way out: the
/// prompt happens once, when the task is created, and never again.
///
/// Only the server is elevated. CaseLight itself talks to it over a TCP socket and has no
/// business holding administrator rights.
/// </summary>
public static class OpenRgbTask
{
    public const string TaskName = "CaseLight OpenRGB";

    /// <summary>
    /// The task definition, as XML rather than a schtasks command line.
    ///
    /// Two reasons. A path with spaces inside the /tr argument has to be quoted inside an
    /// already quoted argument, which schtasks parses in its own way and gets wrong often
    /// enough to be a known nuisance. And the defaults schtasks picks are wrong for a
    /// lighting daemon: it would refuse to start on battery and stop the task after three
    /// days.
    /// </summary>
    static string BuildXml(string exePath, string user)
    {
        string dir = Path.GetDirectoryName(exePath) ?? "";

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Запуск сервера OpenRGB с правами администратора при входе в систему. Задание создано программой CaseLight.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exePath)}</Command>
              <Arguments>--server --startminimized</Arguments>
              <WorkingDirectory>{Escape(dir)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static bool _known;
    static long _knownAt;

    /// <summary>
    /// Whether the task is registered. Reading the list needs no rights.
    ///
    /// The answer is kept for half a minute: this is asked on the way to every server
    /// launch, sometimes from the interface thread, and each question is a process start.
    /// Creating or removing the task answers it again immediately.
    /// </summary>
    public static bool Exists()
    {
        long now = Environment.TickCount64;
        if (_knownAt > 0 && now - _knownAt < 30000) return _known;

        try { _known = Run("schtasks", $"/query /tn \"{TaskName}\"", out _) == 0; }
        catch { _known = false; }

        _knownAt = now;
        return _known;
    }

    /// <summary>Drops the cached answer after the task list has been changed by us.</summary>
    static void Forget() => _knownAt = 0;

    /// <summary>
    /// Registers the task. Raises one UAC prompt, which is the whole point of doing it.
    /// </summary>
    public static string Create(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return Loc.P("OpenRGB.exe не найден, задание не создано", "OpenRGB.exe not found, no task created");

        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string xml = Path.Combine(Path.GetTempPath(), "caselight-openrgb-task.xml");

        try
        {
            // schtasks reads the definition as UTF-16; a UTF-8 file is rejected outright
            File.WriteAllText(xml, BuildXml(exePath, user), Encoding.Unicode);

            if (!Elevated("schtasks", $"/create /tn \"{TaskName}\" /xml \"{xml}\" /f"))
                return Loc.P("создание задания отменено", "task creation cancelled");

            // schtasks returns before the registration is visible, hence the short wait
            Forget();
            for (int i = 0; i < 20 && !Exists(); i++) { Forget(); System.Threading.Thread.Sleep(200); }

            bool ok = Exists();
            ProbeLog.Log(Loc.P("планировщик", "scheduler"), ok ? Loc.P("задание создано: ", "task created: ") + exePath : Loc.P("задание создать не удалось", "the task could not be created"));
            return ok
                ? Loc.P("OpenRGB будет запускаться при входе с правами администратора", "OpenRGB will start at logon with administrator rights")
                : Loc.P("не удалось создать задание", "could not create the task");
        }
        catch (Exception ex)
        {
            ProbeLog.Log(Loc.P("планировщик", "scheduler"), Loc.P("ошибка создания задания: ", "task creation error: ") + ex.Message);
            return Loc.P("не удалось создать задание: ", "could not create the task: ") + ex.Message;
        }
        finally
        {
            try { File.Delete(xml); } catch { /* временный файл, не беда */ }
        }
    }

    public static string Delete()
    {
        try
        {
            if (!Elevated("schtasks", $"/delete /tn \"{TaskName}\" /f"))
                return Loc.P("удаление задания отменено", "task removal cancelled");

            Forget();
            for (int i = 0; i < 20 && Exists(); i++) { Forget(); System.Threading.Thread.Sleep(200); }

            ProbeLog.Log(Loc.P("планировщик", "scheduler"), Loc.P("задание удалено", "task removed"));
            return Loc.P("задание удалено, автозапуск OpenRGB с правами выключен", "task removed, the elevated autostart of OpenRGB is off");
        }
        catch (Exception ex)
        {
            return Loc.P("не удалось удалить задание: ", "could not remove the task: ") + ex.Message;
        }
    }

    /// <summary>
    /// Starts the server through the task, which is how it gets its rights without a prompt.
    ///
    /// Starting an existing task on demand is allowed for the account that owns it, even
    /// though the task itself runs elevated - so this is the one path that can bring the
    /// server back after sleep without asking the user for anything.
    /// </summary>
    public static bool TryStart()
    {
        try
        {
            if (!Exists()) return false;

            int code = Run("schtasks", $"/run /tn \"{TaskName}\"", out string output);
            bool ok = code == 0;

            ProbeLog.Log(Loc.P("планировщик", "scheduler"), ok ? Loc.P("сервер запущен заданием", "server started by the task") : Loc.P("запуск заданием не удался: ", "starting by the task failed: ") + output.Trim());
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the server through the task, which is the only way we have of stopping it.
    ///
    /// A server started by the task runs elevated, and this program deliberately does not.
    /// From below, a window message is dropped by the integrity check and Kill comes back
    /// with access denied - both were measured, not guessed. The scheduler ends the task in
    /// its own security context, so it needs no rights from us.
    /// </summary>
    public static bool TryStop()
    {
        try
        {
            if (!Exists()) return false;

            int code = Run("schtasks", $"/end /tn \"{TaskName}\"", out string output);
            bool ok = code == 0;

            ProbeLog.Log(Loc.P("планировщик", "scheduler"), ok ? Loc.P("сервер остановлен заданием", "server stopped by the task") : Loc.P("остановка заданием не удалась: ", "stopping by the task failed: ") + output.Trim());
            return ok;
        }
        catch
        {
            return false;
        }
    }

    static int Run(string file, string args, out string output)
    {
        var info = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(info);
        if (process == null) { output = ""; return -1; }

        output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return process.HasExited ? process.ExitCode : -1;
    }

    /// <summary>Runs a command through the shell so it can ask for rights. False means declined.</summary>
    static bool Elevated(string file, string args)
    {
        var info = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(info);
            process?.WaitForExit(30000);
            return process != null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223: the user closed the UAC prompt. Not an error worth a stack trace.
            return false;
        }
    }
}
