namespace Mucka.Core;

/// <summary>
/// Snapshot of the user-tunable client settings that round-trip between the game view,
/// the settings dialog, and mucka.ini. One object travels in and out so a new setting
/// cannot be half-plumbed (the cause of the lost font-size/volume bugs).
/// </summary>
public sealed record ClientSettings
{
    /// <summary>Terminal font size in pixels (9–24).</summary>
    public int FontSize { get; init; }
    /// <summary>Maximum terminal columns advertised to the server (40–160).</summary>
    public int MaxColumns { get; init; }
    /// <summary>Sound volume, 0–100.</summary>
    public int Volume { get; init; }
    /// <summary>FES stats-update heartbeat interval in seconds; 0 disables it.</summary>
    public int StatUpdateFrequency { get; init; }
    /// <summary>Bell muted for this session only — never persisted.</summary>
    public bool MuteBeepSession { get; init; }
    /// <summary>Bell muted permanently — persisted to mucka.ini.</summary>
    public bool MuteBeepPermanently { get; init; }
    /// <summary>Save the settings values to the per-profile [settings:Name] ini section
    /// instead of the global [settings]. Mirrors the "Save to profile only" checkbox.</summary>
    public bool SettingsPerProfile { get; init; }
    /// <summary>Save the fkeys to the per-profile [fkeys:Name] ini section instead of the
    /// global [fkeys]. Mirrors the Hotkeys page's "Save to profile only" checkbox.</summary>
    public bool FkeysPerProfile { get; init; }
    /// <summary>Per-sound enablement and group fallbacks (the Sounds tab's tree).
    /// Treated as frozen once snapshotted — see <see cref="SoundSettings"/>.</summary>
    public SoundSettings Sounds { get; init; } = new();

    // ── Display tab settings (always global, never per-profile) ──────────────
    /// <summary>Global default terminal font size in pixels; 0 = use built-in default.</summary>
    public int DefaultFontSize { get; init; }
    /// <summary>Global default maximum terminal columns; 0 = auto-size.</summary>
    public int DefaultMaxColumns { get; init; }
    /// <summary>Dreamword pill font size adjustment relative to the base size. Range -2 to +4.</summary>
    public int DreamwordSizeOffset { get; init; }
    /// <summary>Show the Online (FEW) section in the side panel. When false, FEW is not requested.</summary>
    public bool ShowOnline { get; init; } = true;
    /// <summary>Show the Inventory (carrying half of FEI) in the side panel.</summary>
    public bool ShowInventory { get; init; } = true;
    /// <summary>Show the Items Here (room half of FEI) in the side panel. When both this and
    /// ShowInventory are false, FEI is not requested.</summary>
    public bool ShowItemsHere { get; init; } = true;
    /// <summary>Show the Map/Compass section in the side panel.</summary>
    public bool ShowMapCompass { get; init; } = true;
}
