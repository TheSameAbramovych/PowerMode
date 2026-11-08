namespace PowerMode.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>A one-line reason for a failed run, for lever results.</summary>
    public string Describe(string tool)
    {
        if (TimedOut)
        {
            return $"{tool} timed out";
        }

        var message = (string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError).Trim();
        var firstLine = message.Split('\n', 2)[0].Trim();
        return firstLine.Length > 0 ? $"{tool}: {firstLine}" : $"{tool} exited with code {ExitCode}";
    }
}

/// <summary>Runs a console tool with a timeout and captured output. Never throws for a non-zero exit.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}
