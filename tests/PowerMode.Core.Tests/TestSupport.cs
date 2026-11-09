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

    public LeverRetry? Retry { get; init; }

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

    /// <summary>The next this many captures throw (a DDC read that fails for a moment).</summary>
    public int FailCaptures { get; set; }

    public FakeLever Lever() => new(LeverKind.Brightness)
    {
        OnCapture = s =>
        {
            if (FailCaptures > 0)
            {
                FailCaptures--;
                throw new LeverException("DDC/CI read failed");
            }

            s.Monitors = [new MonitorLevel("m#0", Value, 50)];
        },
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

/// <summary>A power scheme "machine": the active scheme, like powercfg /getactivescheme and /setactive.</summary>
internal sealed class SchemeMachine
{
    public static readonly Guid UserScheme = Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790");

    public Guid Active { get; set; } = UserScheme;

    public bool FailCaptures { get; set; }

    public FakeLever Lever() => new(LeverKind.PowerScheme)
    {
        OnCapture = s => s.ActiveScheme = FailCaptures ? throw new LeverException("powercfg timed out") : Active,
        OnPlan = context =>
        {
            if (context.Target.Scheme is not { } target)
            {
                return LeverPlan.None;
            }

            return target.Scheme == Active
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction("activate", _ =>
                {
                    Active = target.Scheme;
                    return Task.CompletedTask;
                })]);
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
        Delay = new FakeDelay(Time);
        Switcher = new ModeSwitcher(levers, Store, () => Config, () => Elevated, Time, Log, new LeverExecutor(Log, delay: Delay.Wait));
    }

    public TempDir Dir { get; } = new();

    public ListLog Log { get; } = new();

    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Retry waits return at once and advance <see cref="Time"/>.</summary>
    public FakeDelay Delay { get; }

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

/// <summary>A delay that returns at once, records what was asked and advances the clock by it.</summary>
internal sealed class FakeDelay(ManualTime time)
{
    public List<TimeSpan> Waits { get; } = [];

    public Task Wait(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Waits)
        {
            Waits.Add(span);
        }

        time.Advance(span);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A display and its monitor: a new display mode makes the monitor re-sync, and until <see cref="AnswersFrom"/> it does
/// not answer DDC/CI (the brightness plan reports it unavailable), like the owner's monitor after 200 Hz -> 60 Hz.
/// </summary>
internal sealed class ResyncingMonitor(ManualTime time)
{
    public const string NoMonitor = "No monitor answered over DDC/CI (asleep or DDC/CI off)";

    public static readonly LeverRetry Retry = new(TimeSpan.FromSeconds(2), 6, TimeSpan.FromSeconds(1));

    public int RefreshHz { get; set; } = 200;

    public int Brightness { get; set; } = 80;

    /// <summary>How long the monitor stays silent after a mode change.</summary>
    public TimeSpan Resync { get; set; } = TimeSpan.FromSeconds(2.5);

    public DateTimeOffset AnswersFrom { get; private set; } = DateTimeOffset.MinValue;

    public int BrightnessPlans { get; private set; }

    public FakeLever Display() => new(LeverKind.DisplayMode)
    {
        OnCapture = s => s.Displays = [new DisplayModeInfo(@"\.\DISPLAY1", 2560, 1440, RefreshHz, 32)],
        OnPlan = context =>
        {
            if (context.Target.Refresh is not { } target)
            {
                return LeverPlan.None;
            }

            var hz = target.RefreshHz ?? target.Restore.Single().RefreshHz;
            return RefreshRates.Same(hz, RefreshHz)
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction($"{RefreshHz} Hz -> {hz} Hz", _ =>
                {
                    RefreshHz = hz;
                    AnswersFrom = time.Now + Resync;
                    return Task.CompletedTask;
                })]);
        },
    };

    public FakeLever Monitor() => new(LeverKind.Brightness)
    {
        RunsAfter = LeverKind.DisplayMode,
        Retry = Retry,
        OnCapture = s => s.Monitors = [new MonitorLevel(@"\.\DISPLAY1#0", Brightness, 50)],
        OnPlan = context =>
        {
            BrightnessPlans++;
            if (context.Target.Monitor is not { } target)
            {
                return LeverPlan.None;
            }

            if (time.Now < AnswersFrom)
            {
                return LeverPlan.Fail(NoMonitor);
            }

            var wanted = target.BrightnessPercent ?? target.Restore.Single().Brightness;
            return wanted == Brightness
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction($"brightness {Brightness} -> {wanted}", _ =>
                {
                    Brightness = wanted;
                    return Task.CompletedTask;
                })]);
        },
    };
}
