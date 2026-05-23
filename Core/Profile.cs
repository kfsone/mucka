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
    public string[] Fkeys { get => _fkeys; set => _fkeys = NormalizeFkeys(value); }

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
