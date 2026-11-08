using System.Globalization;
using PowerMode.Core.Config;
using PowerMode.Core.Infrastructure;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Parsing;

namespace PowerMode.App.Levers;

/// <summary>
/// Power scheme lever: creates the two PowerMode schemes once (<c>/duplicatescheme</c>), rewrites their values
/// on every apply so config edits take effect, and activates the target scheme. Never writes to any other
/// scheme. Reads with <c>/qh</c> so the hidden PERFBOOSTMODE is visible too.
/// </summary>
internal sealed class PowercfgLever : ILever
{
    public const string Tool = "powercfg";
    private static readonly string ToolPath = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;

    public PowercfgLever(IProcessRunner runner) => _runner = runner;

    public LeverKind Kind => LeverKind.PowerScheme;

    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.ActiveScheme = await ActiveSchemeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Scheme is not { } target)
        {
            return LeverPlan.None;
        }

        var schemes = PowercfgParser.ParseSchemeList(await RunAsync(cancellationToken, "/list").ConfigureAwait(false));
        var active = schemes.FirstOrDefault(s => s.IsActive)?.Id;
        var exists = schemes.Any(s => s.Id == target.Scheme);
        var actions = new List<LeverAction>();

        if (target.Configure is not { } scheme)
        {
            if (!exists)
            {
                return LeverPlan.Fail($"Your scheme {target.Scheme} no longer exists");
            }

            if (active != target.Scheme)
            {
                actions.Add(Action($"Activate your scheme {target.Scheme}", "/setactive", Id(target.Scheme)));
            }

            return LeverPlan.Of(actions);
        }

        if (!exists)
        {
            actions.Add(Action($"Create scheme \"{scheme.Name}\" from {scheme.SourceGuid}", "/duplicatescheme", Id(scheme.SourceGuid), Id(scheme.Id)));
            actions.Add(Action($"Name it \"{scheme.Name}\"", "/changename", Id(scheme.Id), scheme.Name));
        }

        foreach (var (setting, value) in Values(scheme))
        {
            var current = exists ? await ReadAcAsync(scheme.Id, setting, cancellationToken).ConfigureAwait(false) : null;
            if (current != value)
            {
                actions.Add(Action(
                    $"{setting.Name} {(current is { } c ? c.ToString(CultureInfo.InvariantCulture) : "?")} -> {value}",
                    "/setacvalueindex", Id(scheme.Id), Id(setting.Subgroup), Id(setting.Setting), value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // Values of the active scheme only take effect after /setactive, so re-activate after any change.
        if (active != scheme.Id || actions.Count > 0)
        {
            actions.Add(Action($"Activate \"{scheme.Name}\"", "/setactive", Id(scheme.Id)));
        }

        return LeverPlan.Of(actions);
    }

    internal static IEnumerable<(PowerSetting Setting, uint Value)> Values(SchemeConfig scheme)
    {
        yield return (PowerSettings.BoostMode, scheme.Boost ? PowerSettings.BoostAggressive : PowerSettings.BoostDisabled);
        yield return (PowerSettings.MaxProcessor, (uint)scheme.MaxProcessorPercent);
        yield return (PowerSettings.MinProcessor, (uint)scheme.MinProcessorPercent);
        yield return (PowerSettings.DisplayOff, (uint)scheme.DisplayOffSeconds);
        yield return (PowerSettings.Sleep, (uint)scheme.SleepSeconds);
        yield return (PowerSettings.Hibernate, (uint)scheme.HibernateSeconds);
    }

    public async Task<Guid?> ActiveSchemeAsync(CancellationToken cancellationToken) =>
        PowercfgParser.ParseActiveScheme(await RunAsync(cancellationToken, "/getactivescheme").ConfigureAwait(false));

    private async Task<uint?> ReadAcAsync(Guid scheme, PowerSetting setting, CancellationToken cancellationToken)
    {
        var output = await RunAsync(cancellationToken, "/qh", Id(scheme), Id(setting.Subgroup), Id(setting.Setting)).ConfigureAwait(false);
        return PowercfgParser.ParseSettingIndex(output).Ac;
    }

    private LeverAction Action(string description, params string[] arguments) =>
        new(description, async ct => await RunAsync(ct, arguments).ConfigureAwait(false));

    private async Task<string> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await _runner.RunAsync(ToolPath, arguments, Timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new LeverException(result.Describe(Tool));
        }

        return result.StandardOutput;
    }

    private static string Id(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture);
}
