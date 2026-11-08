using System.Security.Principal;
using EZTool.Core;

namespace EZTool.Services;

/// <summary>
/// "Mit Windows starten". EZTool braucht Admin-Rechte – ein normaler Run-Eintrag würde beim Anmelden
/// an der UAC scheitern. Deshalb wird (wie bei vielen System-Tools) eine geplante Aufgabe mit
/// höchsten Rechten angelegt, die EZTool beim Anmelden im Infobereich startet.
/// </summary>
public static class StartupTaskService
{
    public const string TaskName = "EZTool Autostart";

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Loc.T("Settings.TaskSchedulerUnavailable"));
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    public static bool IsEnabled()
    {
        try
        {
            dynamic task = Connect().GetFolder("\\").GetTask(TaskName);
            return (bool)task.Enabled;
        }
        catch { return false; }
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("Settings.ProgramPathUnknown"));
        var user = WindowsIdentity.GetCurrent().Name;

        dynamic svc = Connect();
        dynamic def = svc.NewTask(0);
        def.RegistrationInfo.Description = Loc.T("Settings.StartupTaskDescription");
        def.RegistrationInfo.Author = "EZTool";
        def.Principal.UserId = user;
        def.Principal.LogonType = 3;   // TASK_LOGON_INTERACTIVE_TOKEN
        def.Principal.RunLevel = 1;    // TASK_RUNLEVEL_HIGHEST
        def.Settings.DisallowStartIfOnBatteries = false;
        def.Settings.StopIfGoingOnBatteries = false;
        def.Settings.ExecutionTimeLimit = "PT0S";
        def.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW

        dynamic trigger = def.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = user;
        trigger.Delay = "PT15S";

        dynamic action = def.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = exe;
        action.Arguments = "--tray";
        action.WorkingDirectory = Path.GetDirectoryName(exe);

        // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
        svc.GetFolder("\\").RegisterTaskDefinition(TaskName, def, 6, null, null, 3);
    }

    public static void Disable()
    {
        try { Connect().GetFolder("\\").DeleteTask(TaskName, 0); }
        catch { /* existiert nicht */ }
    }
}
