namespace PowerMode.Core.Cli;

public enum CliCommand
{
    /// <summary>Start the tray app (or forward "open" to the running one).</summary>
    Tray,
    Open,
    Mode,
    Status,
    DryRun,
    RenderStates,
    MakeIcon,
    InstallAutostart,
    UninstallAutostart,
    Help,
}

/// <summary>Parsed command line. <see cref="Error"/> is set when the arguments make no sense.</summary>
public sealed record CliOptions(CliCommand Command, PowerModeKind? Mode = null, string? Directory = null, string? Error = null, string? IconFile = null)
{
    public const string ModeFlag = "--mode";
    public const string StatusFlag = "--status";
    public const string DryRunFlag = "--dry-run";
    public const string OpenFlag = "--open";
    public const string RenderStatesFlag = "--render-states";
    public const string MakeIconFlag = "--make-icon";
    public const string InstallAutostartFlag = "--install-autostart";
    public const string UninstallAutostartFlag = "--uninstall-autostart";
    public const string HelpFlag = "--help";

    public const string Usage = """
        PowerMode - switch this PC between Grid, Station and Reserve.

          PowerMode.exe                          start the tray app
          PowerMode.exe --open                   open the flyout of the running app
          PowerMode.exe --mode grid|station|reserve
                                                 apply a mode (through the running app if any)
          PowerMode.exe --dry-run --mode X       print what would change, change nothing
          PowerMode.exe --status                 print the lever table
          PowerMode.exe --render-states <dir>    render the flyout states to PNG (fake data)
          PowerMode.exe --install-autostart      start at logon (scheduled task, highest privileges)
          PowerMode.exe --uninstall-autostart    remove that task
          PowerMode.exe --make-icon <file.ico>   write the application icon (developer tool)
        """;

    /// <summary>Commands a second instance hands to the running one over the pipe.</summary>
    public bool IsForwardable => Command is CliCommand.Tray or CliCommand.Open or CliCommand.Mode;

    public bool IsValid => Error is null;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        PowerModeKind? mode = null;
        string? directory = null;
        string? iconFile = null;
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case ModeFlag:
                    if (i + 1 >= args.Count || !ModeNames.TryParse(args[i + 1], out var parsed))
                    {
                        return Invalid($"{ModeFlag} needs one of: grid, station, reserve");
                    }

                    mode = parsed;
                    i++;
                    break;
                case RenderStatesFlag:
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Invalid($"{RenderStatesFlag} needs a directory");
                    }

                    directory = args[i + 1];
                    i++;
                    break;
                case MakeIconFlag:
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Invalid($"{MakeIconFlag} needs a file");
                    }

                    iconFile = args[i + 1];
                    i++;
                    break;
                case StatusFlag or DryRunFlag or OpenFlag or InstallAutostartFlag or UninstallAutostartFlag or HelpFlag or "-h" or "/?":
                    flags.Add(arg);
                    break;
                default:
                    return Invalid($"Unknown argument: {arg}");
            }
        }

        if (flags.Contains(HelpFlag) || flags.Contains("-h") || flags.Contains("/?"))
        {
            return new CliOptions(CliCommand.Help);
        }

        if (iconFile is not null)
        {
            return new CliOptions(CliCommand.MakeIcon, IconFile: iconFile);
        }

        if (directory is not null)
        {
            return new CliOptions(CliCommand.RenderStates, Directory: directory);
        }

        if (flags.Contains(DryRunFlag))
        {
            return mode is null ? Invalid($"{DryRunFlag} needs {ModeFlag} X") : new CliOptions(CliCommand.DryRun, mode);
        }

        if (flags.Contains(StatusFlag))
        {
            return new CliOptions(CliCommand.Status);
        }

        if (flags.Contains(InstallAutostartFlag))
        {
            return new CliOptions(CliCommand.InstallAutostart);
        }

        if (flags.Contains(UninstallAutostartFlag))
        {
            return new CliOptions(CliCommand.UninstallAutostart);
        }

        if (mode is not null)
        {
            return new CliOptions(CliCommand.Mode, mode);
        }

        return new CliOptions(flags.Contains(OpenFlag) ? CliCommand.Open : CliCommand.Tray);
    }

    /// <summary>The single line sent over the named pipe.</summary>
    public string ToPipeMessage() => Command == CliCommand.Mode && Mode is { } m ? $"{ModeFlag} {ModeNames.Key(m)}" : OpenFlag;

    /// <summary>The console line once the command has been handed to the running instance.</summary>
    public string ForwardedText => $"Sent to the running PowerMode: {ToPipeMessage()}";

    public static CliOptions FromPipeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var parsed = Parse(message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return parsed.IsValid && parsed.IsForwardable ? parsed : new CliOptions(CliCommand.Open);
    }

    private static CliOptions Invalid(string error) => new(CliCommand.Help, Error: error);
}
