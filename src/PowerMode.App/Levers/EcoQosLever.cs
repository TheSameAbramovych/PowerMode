using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;
using PowerMode.Core.State;

namespace PowerMode.App.Levers;

/// <summary>
/// Efficiency mode (EcoQoS via SetProcessInformation(ProcessPowerThrottling) + idle priority) on the listed
/// processes. Every process it touches is recorded in state.json with its previous priority, so leaving the
/// mode clears exactly those.
/// </summary>
internal sealed class EcoQosLever : ILever
{
    private readonly Lock _gate = new();

    public LeverKind Kind => LeverKind.Efficiency;

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var names = context.Target.Efficiency.ProcessNames;
        var records = context.State.EfficiencyApplied;
        if (names.Count == 0 && records.Count == 0)
        {
            return Task.FromResult(LeverPlan.None);
        }

        var actions = new List<LeverAction>();
        var stale = records.Where(r => !names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var group in stale.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            actions.Add(new LeverAction($"Clear efficiency mode on {FriendlyNames.Process(group.Key)}", _ => Clear(records, items)));
        }

        foreach (var name in names)
        {
            var processes = ProcessInfo.ByName(name);
            var fresh = processes.Count(p => !records.Any(r => Matches(r, p)));
            ProcessInfo.DisposeAll(processes);
            if (fresh > 0)
            {
                actions.Add(new LeverAction($"Efficiency mode on {FriendlyNames.Process(name)} ({fresh} {(fresh == 1 ? "process" : "processes")})", _ => Set(records, name)));
            }
        }

        return Task.FromResult(LeverPlan.Of(actions));
    }

    private Task Set(List<EfficiencyRecord> records, string name)
    {
        var processes = ProcessInfo.ByName(name);
        try
        {
            var denied = 0;
            foreach (var process in processes)
            {
                lock (_gate)
                {
                    if (records.Any(r => Matches(r, process)))
                    {
                        continue;
                    }
                }

                if (TrySet(process, on: true, ProcessPriorityClass.Idle) is { } previous)
                {
                    lock (_gate)
                    {
                        records.Add(new EfficiencyRecord(process.Id, name, ProcessInfo.TryGetStart(process) ?? default, (int)previous));
                    }
                }
                else
                {
                    denied++;
                }
            }

            if (denied == processes.Length && denied > 0)
            {
                throw new LeverException($"{FriendlyNames.Process(name)}: access denied", needsElevation: !Elevation.IsElevated);
            }
        }
        finally
        {
            ProcessInfo.DisposeAll(processes);
        }

        return Task.CompletedTask;
    }

    private Task Clear(List<EfficiencyRecord> records, List<EfficiencyRecord> items)
    {
        foreach (var record in items)
        {
            try
            {
                using var process = Process.GetProcessById(record.ProcessId);
                if (Matches(record, process))
                {
                    TrySet(process, on: false, (ProcessPriorityClass)record.PreviousPriority);
                }
            }
            catch (ArgumentException)
            {
                // The process has exited; nothing to clear.
            }

            lock (_gate)
            {
                records.Remove(record);
            }
        }

        return Task.CompletedTask;
    }

    private static bool Matches(EfficiencyRecord record, Process process) =>
        record.ProcessId == process.Id && (ProcessInfo.TryGetStart(process) is not { } start || start == record.ProcessStart);

    /// <summary>Turn EcoQoS on/off and set the priority; returns the previous priority, or null when access was denied.</summary>
    private static ProcessPriorityClass? TrySet(Process process, bool on, ProcessPriorityClass priority)
    {
        try
        {
            var previous = process.PriorityClass;
            using var handle = ProcessInfo.Open(process.Id, Kernel32.ProcessSetInformation | Kernel32.ProcessQueryLimitedInformation);
            if (handle.IsInvalid)
            {
                return null;
            }

            var state = new Kernel32.PowerThrottlingState
            {
                Version = Kernel32.PowerThrottlingCurrentVersion,
                ControlMask = on ? Kernel32.PowerThrottlingExecutionSpeed : 0,
                StateMask = on ? Kernel32.PowerThrottlingExecutionSpeed : 0,
            };
            if (!Kernel32.SetProcessInformation(handle, Kernel32.ProcessPowerThrottling, ref state, Marshal.SizeOf<Kernel32.PowerThrottlingState>()))
            {
                return null;
            }

            process.PriorityClass = priority;
            return previous;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
