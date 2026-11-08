using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;
using PowerMode.Core.Model;
using PowerMode.Core.State;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];

    public void Write(string category, string message)
    {
        lock (Lines)
        {
            Lines.Add($"[{category}] {message}");
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PowerModeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>A lever with scripted capture/plan behaviour.</summary>
internal sealed class FakeLever(LeverKind kind) : ILever
{
    public LeverKind Kind { get; } = kind;

    public LeverKind? RunsAfter { get; init; }

    public Action<Snapshot>? OnCapture { get; init; }

    public Func<ApplyContext, LeverPlan> OnPlan { get; init; } = _ => LeverPlan.None;

    public int Captures { get; private set; }

    public List<TargetState> Planned { get; } = [];

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        Captures++;
        OnCapture?.Invoke(snapshot);
        return Task.CompletedTask;
    }

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        lock (Planned)
        {
            Planned.Add(context.Target);
        }

        return Task.FromResult(OnPlan(context));
    }
}

/// <summary>A brightness "machine": captures, plans the diff and applies it, like the real DDC lever.</summary>
internal sealed class BrightnessMachine
{
    public int Value { get; set; } = 80;

    public int Writes { get; private set; }

    public bool FailWrites { get; set; }

    public FakeLever Lever() => new(LeverKind.Brightness)
    {
        OnCapture = s => s.Monitors = [new MonitorLevel("m#0", Value, 50)],
        OnPlan = context =>
        {
            if (context.Target.Monitor is not { } target)
            {
                return LeverPlan.None;
            }

            var wanted = target.BrightnessPercent ?? target.Restore.Single().Brightness;
            return wanted == Value
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction($"brightness {Value} -> {wanted}", _ =>
                {
                    if (FailWrites)
                    {
                        throw new LeverException("monitor asleep");
                    }

                    Value = wanted;
                    Writes++;
                    return Task.CompletedTask;
                })]);
        },
    };
}

/// <summary>A GPU "machine" whose writes need elevation.</summary>
internal sealed class GpuMachine
{
    public uint LimitMw { get; set; } = 120_000;

    public FakeLever Lever() => new(LeverKind.Gpu)
    {
        OnCapture = s => s.Gpu = new GpuSnapshot(LimitMw, 120_000),
        OnPlan = context =>
        {
            if (context.Target.Gpu is not { } target)
            {
                return LeverPlan.None;
            }

            var wanted = target.RestoreMilliwatts ?? (uint)target.LimitWatts!.Value * 1000;
            return wanted == LimitMw
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction("limit", _ =>
                {
                    LimitMw = wanted;
                    return Task.CompletedTask;
                }, RequiresElevation: true)]);
        },
    };
}

internal sealed class SwitcherHarness : IDisposable
{
    public SwitcherHarness(IReadOnlyList<ILever> levers, bool elevated = true, AppConfig? config = null)
    {
        Config = config ?? AppConfig.CreateDefault();
        Store = new StateStore(Dir.File("state.json"), Log);
        Elevated = elevated;
        Switcher = new ModeSwitcher(levers, Store, () => Config, () => Elevated, Time, Log);
    }

    public TempDir Dir { get; } = new();

    public ListLog Log { get; } = new();

    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    public AppConfig Config { get; set; }

    public bool Elevated { get; set; }

    public StateStore Store { get; }

    public ModeSwitcher Switcher { get; }

    public PersistedState Reload() => Store.Load();

    public void Dispose()
    {
        Switcher.Dispose();
        Dir.Dispose();
    }
}
