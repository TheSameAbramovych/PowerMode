using System.Text;
using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.Core;
using PowerMode.Core.Cli;
using PowerMode.Core.Presentation;

namespace PowerMode.App;

/// <summary>The console commands: --status, --dry-run, --mode without a running instance, autostart.</summary>
internal static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitFailedLevers = 2;
    private static readonly TimeSpan CounterSampleGap = TimeSpan.FromSeconds(1);

    /// <summary>A WinExe has no console: attach to the parent's when output is not redirected.</summary>
    public static void AttachConsole()
    {
        if (Kernel32.GetStdHandle(Kernel32.StdOutputHandle) == IntPtr.Zero)
        {
            // Not redirected: write to the parent console (Unicode via WriteConsoleW).
            Kernel32.AttachConsole(Kernel32.AttachParentProcess);
            return;
        }

        // Redirected to a pipe or file. Open the inherited handles first, then ask the parent's console how it decodes:
        // UTF-8 (or no console at all: a service, a task, a program reading the pipe) keeps "·" and "≈"; an OEM code
        // page (Windows PowerShell 5.1 decodes native output with it) gets ASCII look-alikes instead of mojibake.
        var output = Console.OpenStandardOutput();
        var error = Console.OpenStandardError();
        var codePage = 0u;
        if (Kernel32.AttachConsole(Kernel32.AttachParentProcess))
        {
            codePage = Kernel32.GetConsoleOutputCP();
            Kernel32.FreeConsole();
        }

        Console.SetOut(Writer(output, codePage));
        Console.SetError(Writer(error, codePage));
    }

    private static TextWriter Writer(Stream stream, uint codePage) => ConsoleText.NeedsAscii(codePage)
        ? new AsciiWriter(new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true })
        : new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>Maps the report symbols to ASCII (see <see cref="ConsoleText"/>); the ASCII encoder writes "?" for the rest.</summary>
    private sealed class AsciiWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => inner.Write(ConsoleText.Map(value));

        public override void Write(string? value) => inner.Write(value is null ? null : ConsoleText.ToAscii(value));

        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static int Help(CliOptions options)
    {
        if (options.Error is { } error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
        }

        Console.WriteLine(CliOptions.Usage);
        return options.IsValid ? ExitOk : ExitError;
    }

    public static async Task<int> StatusAsync()
    {
        using var services = AppServices.Create();
        services.RefreshMachine();
        await Task.Delay(CounterSampleGap).ConfigureAwait(false);
        var live = await services.ReadLiveAsync(flyoutVisible: true, allowDdc: true).ConfigureAwait(false);
        var autostart = await services.Autostart.IsInstalledAsync(CancellationToken.None).ConfigureAwait(false);
        Console.Write(TextReports.Status(services.BuildState(live, autostart, power: services.PowerCardOf(live))));
        return ExitOk;
    }

    public static async Task<int> DryRunAsync(PowerModeKind mode)
    {
        using var services = AppServices.Create();
        var report = await services.Switcher.DryRunAsync(mode).ConfigureAwait(false);
        Console.Write(TextReports.DryRun(report));
        return ExitOk;
    }

    /// <summary>--mode without a running tray instance: apply here, print the table, exit.</summary>
    public static async Task<int> ApplyAsync(PowerModeKind mode)
    {
        using var services = AppServices.Create();
        await services.RecoverLostModeAsync().ConfigureAwait(false);
        var outcome = await services.Switcher.SwitchAsync(mode).ConfigureAwait(false);
        var live = await services.ReadLiveAsync(flyoutVisible: true, allowDdc: true).ConfigureAwait(false);
        var autostart = await services.Autostart.IsInstalledAsync(CancellationToken.None).ConfigureAwait(false);
        Console.Write(TextReports.Status(services.BuildState(live, autostart, power: services.PowerCardOf(live))));
        return outcome.FailedCount == 0 ? ExitOk : ExitFailedLevers;
    }

    public static async Task<int> AutostartAsync(bool install)
    {
        if (!Elevation.IsElevated)
        {
            // Creating a task with highest privileges needs an administrator: re-run this command elevated.
            var flag = install ? CliOptions.InstallAutostartFlag : CliOptions.UninstallAutostartFlag;
            var code = Elevation.StartElevated([flag], waitForExit: true);
            Console.WriteLine(code switch
            {
                null => "Cancelled at the administrator prompt.",
                ExitOk => install ? "Autostart task installed." : "Autostart task removed.",
                _ => $"Failed (exit code {code}); see the log in {AppPaths.LogDirectory}.",
            });
            return code ?? ExitError;
        }

        using var services = AppServices.Create();
        var error = install
            ? await services.Autostart.InstallAsync(CancellationToken.None).ConfigureAwait(false)
            : await services.Autostart.UninstallAsync(CancellationToken.None).ConfigureAwait(false);
        services.Log.Write("autostart", error ?? (install ? "Installed" : "Removed"));
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return ExitError;
        }

        Console.WriteLine(install ? "Autostart task installed." : "Autostart task removed.");
        return ExitOk;
    }
}
