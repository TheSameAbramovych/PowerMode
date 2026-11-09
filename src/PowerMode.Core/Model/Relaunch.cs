using System.Text.RegularExpressions;

namespace PowerMode.Core.Model;

/// <summary>How Grid starts an app again. <see cref="WorkingDirectory"/> null means the user's profile folder.</summary>
public sealed record LaunchTarget(string File, string Arguments, string? WorkingDirectory, bool IsPackaged, bool ArgumentsDropped);

/// <summary>Choosing what to start when Grid relaunches a closed app, and what to record when it is captured.</summary>
public static partial class Relaunch
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";
    private const string SquirrelUpdater = "Update.exe";

    /// <summary>
    /// A packaged (MSIX) app starts through its AUMID: the shell activates the current package version with its
    /// identity, so a Store update while it was closed does not matter. That route cannot pass arguments. Any other
    /// app starts by its path, in its own folder.
    /// </summary>
    public static LaunchTarget For(CapturedProcess app, string arguments)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(arguments);
        return string.IsNullOrWhiteSpace(app.AppUserModelId)
            ? new LaunchTarget(app.Path, arguments, Path.GetDirectoryName(app.Path), IsPackaged: false, ArgumentsDropped: false)
            : new LaunchTarget(AppsFolderPrefix + app.AppUserModelId, string.Empty, null, IsPackaged: true, ArgumentsDropped: arguments.Length > 0);
    }

    /// <summary>
    /// Squirrel apps (Postman Agent, ...) run from a versioned <c>app-x.y.z</c> folder that an update replaces; their
    /// stable stub sits one level up next to <c>Update.exe</c>. Returns the stub when that layout is there, else the path.
    /// </summary>
    public static string StablePath(string path, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var folder = Path.GetDirectoryName(path);
        var root = folder is null ? null : Path.GetDirectoryName(folder);
        if (folder is null || root is null || !SquirrelAppFolder().IsMatch(Path.GetFileName(folder)))
        {
            return path;
        }

        var stub = Path.Combine(root, Path.GetFileName(path));
        return fileExists(stub) && fileExists(Path.Combine(root, SquirrelUpdater)) ? stub : path;
    }

    [GeneratedRegex(@"^app-\d+(\.\d+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SquirrelAppFolder();
}
