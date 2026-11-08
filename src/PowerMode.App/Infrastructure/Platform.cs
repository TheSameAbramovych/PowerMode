using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PowerMode.App.Infrastructure;

/// <summary>Paths of config, state and logs under %LOCALAPPDATA%\PowerMode, and of this exe.</summary>
internal static class AppPaths
{
    public const string AppName = "PowerMode";

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppName + ".exe");
}

internal static class Elevation
{
    private const int ErrorCancelled = 1223;
    private const string RunAsVerb = "runas";

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Start this exe elevated (UAC prompt). Returns null when the user cancelled the prompt, otherwise the
    /// exit code when <paramref name="waitForExit"/> is set (0 when not waiting).
    /// </summary>
    public static int? StartElevated(IEnumerable<string> arguments, bool waitForExit)
    {
        var info = new ProcessStartInfo(AppPaths.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = RunAsVerb,
            WorkingDirectory = Path.GetDirectoryName(AppPaths.ExecutablePath)!,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (waitForExit && process is not null)
            {
                process.WaitForExit();
                return process.ExitCode;
            }

            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    public static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException
        || exception is Win32Exception { NativeErrorCode: 5 }
        || exception.InnerException is Win32Exception { NativeErrorCode: 5 };
}

/// <summary>Finds a tool on PATH or in its well-known install folder.</summary>
internal static class ExecutableLocator
{
    public static string? Find(string fileName, params string[] knownLocations)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return knownLocations.FirstOrDefault(File.Exists);
    }
}

/// <summary>Native calls that can block (DDC/CI over a sleeping monitor) are given a timeout.</summary>
internal static class NativeCall
{
    public static async Task<T> RunAsync<T>(Func<T> call, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(call, cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new Core.Levers.LeverException($"{what} timed out");
        }
    }
}
