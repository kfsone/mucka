namespace Mucka.Core;

public class Profile
{
    private string[] _fkeys = CreateEmptyFkeys();

    public string Name { get; set; } = "MUD2";
    public string Host { get; set; } = "mud2.co.uk";
    public int Port { get; set; } = 23;
    public string AccountId { get; set; } = string.Empty;
    public bool RememberPassword { get; set; }
    public bool TelnetLoginEnabled { get; set; } = true;
    public string TelnetLoginName { get; set; } = "mud";
    /// <summary>Maximum terminal columns to advertise via NAWS. Clamped to 20–160.</summary>
    public int MaxColumns { get; set; } = 80;
    /// <summary>Seconds of player inactivity before sending a blank keep-alive command. 0 = disabled.</summary>
    public int AntiIdleSeconds { get; set; } = 0;
    /// <summary>Keep the screen/display awake while connected to the game.</summary>
    public bool KeepScreenOn { get; set; } = false;
    /// <summary>Fill unpopulated hotkey slots with built-in defaults at session start.</summary>
    public bool DefaultHotkeys { get; set; } = true;
    public string[] Fkeys { get => _fkeys; set => _fkeys = NormalizeFkeys(value); }

    private static readonly Dictionary<int, string> s_defaultFkeys = new()
    {
        [0] = "l around",
        [1] = "use weap",
        [2] = "open bag,g 1 wafer from bag,eat 1 wafer",
        [3] = "flee o",
        [4] = "qw,qs",
    };

    /// <summary>
    /// Returns the fkeys array with built-in defaults substituted for any empty slots,
    /// when <see cref="DefaultHotkeys"/> is enabled.
    /// </summary>
    public string[] GetEffectiveFkeys()
    {
        var result = (string[])_fkeys.Clone();
        if (!DefaultHotkeys)
            return result;
        foreach (var (i, def) in s_defaultFkeys)
            if (i < result.Length && string.IsNullOrEmpty(result[i]))
                result[i] = def;
        return result;
    }

    private static string[] CreateEmptyFkeys() => new string[36];

    private static string[] NormalizeFkeys(string[]? fkeys)
    {
        var normalized = CreateEmptyFkeys();
        if (fkeys == null)
            return normalized;

        for (var i = 0; i < normalized.Length; i++)
            normalized[i] = i < fkeys.Length ? fkeys[i] ?? string.Empty : string.Empty;

        return normalized;
    }
}
