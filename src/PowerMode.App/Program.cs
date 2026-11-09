using System.Globalization;
using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.App.Ui;
using PowerMode.Core.Cli;
using PowerMode.Core.Logging;

namespace PowerMode.App;

internal static class Program
{
    /// <summary>How long --quit waits for the running instance to end (it finishes a running switch first).</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(120);

    [STAThread]
    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        switch (options.Command)
        {
            case CliCommand.Help:
                CliRunner.AttachConsole();
                return CliRunner.Help(options);
            case CliCommand.Version:
                CliRunner.AttachConsole();
                Console.WriteLine($"PowerMode {MachineProbe.Version}");
                return CliRunner.ExitOk;
            case CliCommand.RenderStates:
                CliRunner.AttachConsole();
                return RenderStates(options.Directory!);
            case CliCommand.MakeIcon:
                CliRunner.AttachConsole();
                IconRenderer.SaveAppIcon(options.IconFile!);
                Console.WriteLine(Path.GetFullPath(options.IconFile!));
                return CliRunner.ExitOk;
            case CliCommand.Status:
                CliRunner.AttachConsole();
                return CliRunner.StatusAsync().GetAwaiter().GetResult();
            case CliCommand.DryRun:
                CliRunner.AttachConsole();
                return CliRunner.DryRunAsync(options.Mode!.Value).GetAwaiter().GetResult();
            case CliCommand.InstallAutostart:
            case CliCommand.UninstallAutostart:
                CliRunner.AttachConsole();
                return CliRunner.AutostartAsync(options.Command == CliCommand.InstallAutostart).GetAwaiter().GetResult();
            default:
                return RunInstance(options, args);
        }
    }

    /// <summary>Tray, --open, --mode and --quit: hand over to the running instance, or become it.</summary>
    private static int RunInstance(CliOptions options, string[] args)
    {
        // First, before anything that could wait: a start that never gets further still leaves a trace.
        var log = LogSinks.Create();
        log.Write(TrayController.Category, string.Create(CultureInfo.InvariantCulture, $"Starting (pid {Environment.ProcessId}{(args.Length > 0 ? ", " + string.Join(' ', args) : string.Empty)})"));
        log.Write(TrayController.Category, LogSinks.DescribeProcess());

        using var instance = new SingleInstance();
        if (!instance.TryAcquire(log))
        {
            return Forward(options, log);
        }

        if (options.Command == CliCommand.Quit)
        {
            log.Write(TrayController.Category, "--quit: PowerMode is not running");
            CliRunner.AttachConsole();
            Console.WriteLine("PowerMode is not running.");
            return CliRunner.ExitOk;
        }

        if (options.Command == CliCommand.Mode)
        {
            CliRunner.AttachConsole();
            return CliRunner.ApplyAsync(options.Mode!.Value).GetAwaiter().GetResult();
        }

        var app = new App();
        using var services = AppServices.Create();
        using var controller = new TrayController(app, services, instance, args);

        // Start inside the message loop: the tray icon comes first, and nothing at start-up runs before the
        // dispatcher does (an exception there reaches the logged DispatcherUnhandledException handler).
        app.Dispatcher.BeginInvoke(() => controller.Start(openFlyout: options.Command == CliCommand.Open));
        return app.Run();
    }

    /// <summary>Another instance runs: hand the command to it. <c>--quit</c> also waits until it has gone.</summary>
    private static int Forward(CliOptions options, FileLog log)
    {
        // Let the running instance bring its flyout to the front.
        User32.AllowSetForegroundWindow(User32.AllowAnyProcess);
        var forwarded = SingleInstance.TryForwardAsync(options.ToPipeMessage()).GetAwaiter().GetResult();
        log.Write(TrayController.Category, forwarded ? $"Handed {options.ToPipeMessage()} to the running instance" : "The running instance did not answer");

        // Started from a shortcut there is no parent console and nothing is printed.
        CliRunner.AttachConsole();
        if (!forwarded)
        {
            Console.Error.WriteLine("PowerMode is running but did not answer.");
            return CliRunner.ExitError;
        }

        Console.WriteLine(options.ForwardedText);
        if (options.Command != CliCommand.Quit)
        {
            return CliRunner.ExitOk;
        }

        // A running switch finishes first (Docker can take a while), then the tray ends.
        if (SingleInstance.WaitUntilGone(QuitTimeout))
        {
            Console.WriteLine("PowerMode has quit; the current mode stays.");
            return CliRunner.ExitOk;
        }

        Console.Error.WriteLine($"PowerMode is still running after {QuitTimeout.TotalSeconds:0} s.");
        return CliRunner.ExitError;
    }

    private static int RenderStates(string directory)
    {
        var app = new App();
        var report = StateRenderer.Run(app, Path.GetFullPath(directory));
        foreach (var path in report.Written)
        {
            Console.WriteLine(path);
        }

        // A state taller than the flyout would scroll: fail, naming it.
        foreach (var overflow in report.Overflows)
        {
            Console.Error.WriteLine(overflow);
        }

        return report.Overflows.Count == 0 ? CliRunner.ExitOk : CliRunner.ExitError;
    }
}
