namespace PowerMode.Core.Model;

/// <summary>Well-known power scheme GUIDs. Scheme names are localized, so only GUIDs are ever compared.</summary>
public static class PowerSchemes
{
    public static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Station = Guid.Parse("7d0a5e01-0000-4000-8000-00504d535441");
    public static readonly Guid Reserve = Guid.Parse("7d0a5e02-0000-4000-8000-00504d525356");

    public const string StationName = "PowerMode Station";
    public const string ReserveName = "PowerMode Reserve";

    public static bool IsOwn(Guid scheme) => scheme == Station || scheme == Reserve;
}

/// <summary>A power setting addressed by subgroup + setting GUID (aliases are avoided; PERFBOOSTMODE is hidden).</summary>
public sealed record PowerSetting(string Name, Guid Subgroup, Guid Setting);

public static class PowerSettings
{
    public static readonly Guid SubProcessor = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid SubVideo = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid SubSleep = Guid.Parse("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    public static readonly PowerSetting BoostMode = new("PERFBOOSTMODE", SubProcessor, Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7"));
    public static readonly PowerSetting MaxProcessor = new("PROCTHROTTLEMAX", SubProcessor, Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec"));
    public static readonly PowerSetting MinProcessor = new("PROCTHROTTLEMIN", SubProcessor, Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c"));
    public static readonly PowerSetting DisplayOff = new("VIDEOIDLE", SubVideo, Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"));
    public static readonly PowerSetting Sleep = new("STANDBYIDLE", SubSleep, Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));
    public static readonly PowerSetting Hibernate = new("HIBERNATEIDLE", SubSleep, Guid.Parse("9d7815a6-7ee4-497e-8888-515a05f02364"));

    /// <summary>PERFBOOSTMODE values: 0 = disabled, 2 = aggressive (the Windows default).</summary>
    public const uint BoostDisabled = 0;
    public const uint BoostAggressive = 2;
}
