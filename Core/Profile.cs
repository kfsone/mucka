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
    /// <summary>Maximum terminal columns to advertise via NAWS. 0 = auto-size to window (default).</summary>
    public int MaxColumns { get; set; } = 0;
    /// <summary>Seconds of player inactivity before sending a blank keep-alive command. 0 = disabled.</summary>
    public int AntiIdleSeconds { get; set; } = 0;
    /// <summary>Keep the screen/display awake while connected to the game.
    /// Defaults on for mobile, where an idle screen-lock mid-session gets you swamped.</summary>
    public bool KeepScreenOn { get; set; } =
#if ANDROID || IOS
        true;
#else
        false;
#endif
    /// <summary>Fill unpopulated hotkey slots with built-in defaults at session start.</summary>
    public bool DefaultHotkeys { get; set; } = true;
    /// <summary>Terminal font size in pixels. 0 = use the built-in default (15px).</summary>
    public int FontSize { get; set; } = 0;
    /// <summary>Sound volume, 0–100. Default 75.</summary>
    public int Volume { get; set; } = 75;
    /// <summary>FES stats-update heartbeat interval in seconds. 0 = disabled. Default 10.</summary>
    public int StatUpdateFrequency { get; set; } = 10;
    /// <summary>Permanently suppress the bell/beep sound. Persisted per profile.</summary>
    public bool MuteBeepPermanently { get; set; }
    /// <summary>Per-sound enablement and group fallbacks. Defaults to everything on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SoundSettings Sounds { get; set; } = new();
    public string[] Fkeys { get => _fkeys; set => _fkeys = NormalizeFkeys(value); }

    /// <summary>True when the settings came from a per-profile [settings:Name] ini section
    /// rather than the global [settings]. Derived from mucka.ini at load; not persisted here.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SettingsPerProfile { get; set; }
    /// <summary>True when the fkeys came from a per-profile [fkeys:Name] ini section
    /// rather than the global [fkeys]. Derived from mucka.ini at load; not persisted here.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FkeysPerProfile { get; set; }

    // ── Display tab settings (global, loaded from [settings] section at connect time) ──
    [System.Text.Json.Serialization.JsonIgnore]
    public int DefaultFontSize { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int DefaultMaxColumns { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int DreamwordSizeOffset { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowOnline { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowInventory { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowItemsHere { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowMapCompass { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public int MaxOnlineDisplay { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OnlineNamesOnly { get; set; }

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
