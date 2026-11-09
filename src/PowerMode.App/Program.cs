using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.App.Ui;
using PowerMode.Core.Cli;

namespace PowerMode.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        switch (options.Command)
        {
            case CliCommand.Help:
                CliRunner.AttachConsole();
                return CliRunner.Help(options);
            case CliCommand.RenderStates:
                CliRunner.AttachConsole();
                return RenderStates(options.Directory!);
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

    /// <summary>Tray, --open and --mode: hand over to the running instance, or become it.</summary>
    private static int RunInstance(CliOptions options, string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            // Let the running instance bring its flyout to the front.
            User32.AllowSetForegroundWindow(User32.AllowAnyProcess);
            var forwarded = SingleInstance.TryForwardAsync(options.ToPipeMessage()).GetAwaiter().GetResult();
            if (options.Command == CliCommand.Mode)
            {
                CliRunner.AttachConsole();
                Console.WriteLine(forwarded ? "Sent to the running PowerMode." : "PowerMode is running but did not answer.");
            }

            return forwarded ? CliRunner.ExitOk : CliRunner.ExitError;
        }

        if (options.Command == CliCommand.Mode)
        {
            CliRunner.AttachConsole();
            return CliRunner.ApplyAsync(options.Mode!.Value).GetAwaiter().GetResult();
        }

        var app = new App();
        using var services = AppServices.Create();
        using var controller = new TrayController(app, services, instance, args);
        controller.Start(openFlyout: options.Command == CliCommand.Open);
        return app.Run();
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
