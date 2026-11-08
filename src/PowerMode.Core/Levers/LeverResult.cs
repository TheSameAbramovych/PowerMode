namespace PowerMode.Core.Levers;

public enum LeverStatus
{
    /// <summary>The lever reached its target (or already was there).</summary>
    Ok,

    /// <summary>The lever could not reach its target; <see cref="LeverResult.Detail"/> says why.</summary>
    Failed,

    /// <summary>The mode does not own this lever; nothing was done.</summary>
    Skipped,
}

/// <summary>Outcome of one lever. Levers never throw; they return one of these.</summary>
public sealed record LeverResult(LeverStatus Status, string Detail, bool NeedsElevation = false, TimeSpan Duration = default)
{
    public const string NeedsAdministrator = "Needs administrator";
    public const string AlreadySet = "Already set";
    public const string NotOwned = "Not used in this mode";

    public static LeverResult Ok(string detail) => new(LeverStatus.Ok, detail);

    public static LeverResult Failed(string reason, bool needsElevation = false) => new(LeverStatus.Failed, reason, needsElevation);

    public static LeverResult Skipped(string reason) => new(LeverStatus.Skipped, reason);

    public bool IsFailure => Status == LeverStatus.Failed;
}

/// <summary>Thrown inside a <see cref="LeverAction"/> to report a readable failure reason.</summary>
public sealed class LeverException : Exception
{
    public LeverException()
    {
    }

    public LeverException(string message) : base(message)
    {
    }

    public LeverException(string message, bool needsElevation) : base(message) => NeedsElevation = needsElevation;

    public LeverException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public bool NeedsElevation { get; }
}
