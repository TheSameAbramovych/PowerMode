using System.Security;
using System.Security.Principal;
using System.Text;
using PowerMode.App.Infrastructure;
using PowerMode.Core.Infrastructure;

namespace PowerMode.App.Levers;

/// <summary>
/// The scheduled task "PowerMode": at logon of this user, highest privileges (so GPU limit and services work
/// without a UAC prompt), no battery conditions, no time limit, started in the exe folder.
/// </summary>
internal sealed class AutostartTask
{
    public const string TaskName = "PowerMode";
    private static readonly string SchtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;

    public AutostartTask(IProcessRunner runner) => _runner = runner;

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(SchtasksPath, ["/query", "/tn", TaskName], Timeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>Create the task. Must run elevated (a task with highest privileges needs an administrator).</summary>
    public async Task<string?> InstallAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(Path.GetTempPath(), $"PowerMode-task-{Environment.ProcessId}.xml");
        await File.WriteAllTextAsync(file, TaskXml(AppPaths.ExecutablePath), Encoding.Unicode, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _runner.RunAsync(SchtasksPath, ["/create", "/tn", TaskName, "/xml", file, "/f"], Timeout, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? null : result.Describe("schtasks");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public async Task<string?> UninstallAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(SchtasksPath, ["/delete", "/tn", TaskName, "/f"], Timeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? null : result.Describe("schtasks");
    }

    internal static string TaskXml(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = SecurityElement.Escape(identity.User!.Value);
        var command = SecurityElement.Escape(exePath);
        var directory = SecurityElement.Escape(Path.GetDirectoryName(exePath)!);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>PowerMode tray app (Grid / Station / Reserve).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
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
                  <Command>{command}</Command>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
