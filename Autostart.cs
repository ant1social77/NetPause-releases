using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace NetPause;

/// <summary>
/// Автозапуск через Планировщик заданий: только так программа с правами
/// администратора может стартовать при входе в Windows без запроса UAC.
/// </summary>
public static class Autostart
{
    const string TaskName = "NetPause";

    static int Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        p.WaitForExit(15000);
        return p.HasExited ? p.ExitCode : -1;
    }

    public static bool IsEnabled()
    {
        try { return Run($"/Query /TN {TaskName}") == 0; }
        catch { return false; }
    }

    public static bool Set(bool on)
    {
        try
        {
            if (!on) return Run($"/Delete /TN {TaskName} /F") == 0;

            var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            var exe = SecurityElement.Escape(Environment.ProcessPath ?? "");
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Запускает NetPause при входе в Windows.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
            // пишем в папку, доступную на запись только администраторам, а не в общий TEMP
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPause");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { return Run($"/Create /TN {TaskName} /XML \"{file}\" /F") == 0; }
            finally { try { File.Delete(file); } catch { } }
        }
        catch { return false; }
    }
}
