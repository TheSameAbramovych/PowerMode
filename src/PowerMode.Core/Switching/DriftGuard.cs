namespace PowerMode.Core.Switching;

/// <summary>Never fight the user more than once per <see cref="MinInterval"/>.</summary>
public sealed class DriftGuard
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private DateTimeOffset? _last;

    public DriftGuard(TimeProvider time) => _time = time;

    /// <summary>True (and the window restarts) when the last correction is at least a minute old.</summary>
    public bool TryEnter()
    {
        var now = _time.GetUtcNow();
        if (_last is { } last && now - last < MinInterval)
        {
            return false;
        }

        _last = now;
        return true;
    }
}
