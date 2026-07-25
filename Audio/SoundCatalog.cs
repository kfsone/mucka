namespace Mucka.Audio;

/// <summary>One shipped sound effect: an identifying code, a human-readable name, and
/// optionally an explicit asset path. Clio effects derive their asset from the numeric code
/// (<c>sounds/clio.{Code}.wav</c>); prefixed families (e.g. the tell alerts) carry an explicit
/// <paramref name="Asset"/> so the catalog is not limited to numeric clio codes.</summary>
public sealed record SoundDef(string Code, string Name, string? Asset = null)
{
    /// <summary>App-package-relative asset path, e.g. "sounds/clio.0703.wav" or "sounds/tell.wav".</summary>
    public string AssetName => Asset ?? $"sounds/clio.{Code}.wav";
}

/// <summary>A family of sound effects sharing a code prefix. <paramref name="HasFallback"/> is
/// true for clio families (the server can trigger a code with no shipped wav → group fallback);
/// false for explicit families like the tell alerts, which never need a fallback pick.</summary>
public sealed record SoundGroupDef(string Prefix, string Name, SoundDef[] Sounds, bool HasFallback = true)
{
    public bool Contains(string code)
    {
        foreach (var s in Sounds)
            if (s.Code == code) return true;
        return false;
    }
}

/// <summary>
/// The fixed catalog of server-triggered sound effects: every clio.*.wav shipped in
/// Resources/Raw/sounds, grouped by FE-code family (see docs/fecodes.txt and the C1 decoder).
/// The settings dialog builds its tree from this; SoundService consults it to gate
/// playback and to detect codes with no wav of their own (group-default fallback).
/// </summary>
public static class SoundCatalog
{
    public static readonly SoundGroupDef[] Groups =
    {
        new("06", "Alerts", new SoundDef[]
        {
            new("06", "Information alert"),
        }),
        new("tell", "Tells", new SoundDef[]
        {
            new("tell",       "Tell (normal)",    "sounds/tell.wav"),
            new("tell-invis", "Tell (invisible)", "sounds/tell-invis.wav"),
            new("tell-wiz",   "Tell (wizard)",    "sounds/tell-wiz.wav"),
        }, HasFallback: false),
        new("07", "Combat hits", new SoundDef[]
        {
            new("070000", "Hit (generic)"),
            new("070001", "Eros hits"),
            new("070002", "Hit against an inanimate object"),
            new("070100", "Bite"),
            new("070101", "Rat bite"),
            new("070200", "Sting"),
            new("070201", "Bee sting"),
            new("070202", "Jellyfish sting"),
            new("070203", "Electric eel sting"),
            new("0703",   "Kick"),
            new("0704",   "Throw"),
            new("0705",   "Captured"),
            new("0706",   "Ghost hit"),
        }),
        new("08", "Fight events", new SoundDef[]
        {
            new("0801", "You hit them"),
            new("0803", "They hit you"),
        }),
        new("11", "Spells", new SoundDef[]
        {
            new("1100", "Disabling spell starts"),
            new("1101", "Disabling spell ends"),
            new("1102", "Enhancing spell starts"),
            new("1103", "Enhancing spell ends"),
            new("1104", "CHANGE spell"),
            new("1105", "DETECT spell"),
            new("1107", "FORCE spell"),
            new("1108", "IGNITE spell"),
            new("1110", "REPAIR spell"),
            new("1111", "SLEEP spell"),
            new("1112", "SNOOP spell"),
            new("1113", "UNSNOOP spell"),
            new("1115", "TRACK spell"),
            new("1116", "UNTRACK spell"),
            new("1117", "UNSITE spell"),
            new("1118", "CHANCE spell"),
            new("1119", "DIAGNOSE spell"),
            new("1120", "Super disabling spell starts"),
            new("1121", "Super disabling spell ends"),
        }),
        new("13", "Distant sounds", new SoundDef[]
        {
            new("1301", "Ox's \"MOO\""),
            new("1302", "Swamp exploding on a lit brand"),
            new("1303", "Lion's \"ROARRRR\""),
            new("1304", "Rumble of thunder"),
            new("1305", "Crack of thunder"),
            new("1306", "Piercing scream"),
            new("1307", "Incredibly loud >*B*O*O*M*<"),
            new("1308", "Bell tolling for a dead magic-user"),
            new("1309", "Bangers' \"BANG\""),
            new("1310", "Wolf's \"AAAOOOOOOHHHH\""),
            new("1311", "Dragon's roar"),
            new("1312", "The bell being struck"),
            new("1313", "The cannon's >CRACK<"),
            new("1314", "The flute"),
            new("1315", "Clear tones of the horn"),
            new("1316", "Badly-tuned horn"),
            new("1317", "The conch"),
            new("1318", "Thunderous roar of a FOD"),
            new("1319", "Whistling feedback of a failed FOD"),
            new("1320", "Tin drum"),
            new("1321", "Rock splitting"),
            new("1322", "Whistle"),
            new("1323", "Warning siren"),
            new("1324", "Bottle >POP<"),
            new("1325", "Dragon's \"HAWUMPH\""),
            new("1326", "Mine flooding"),
            new("1327", "Mine emptying"),
        }),
        new("14", "Weather", new SoundDef[]
        {
            new("140302", "Rain on the trees"),
        }),
        new("18", "Touchstones & chutes", new SoundDef[]
        {
            new("1800", "Touchstone success"),
            new("1801", "Touchstone failure"),
            new("1802", "Fall into the chute"),
            new("1803", "Bump in the chute"),
            new("1804", "Emerge from the chute"),
            new("1806", "Land safely below the cliff"),
        }),
    };

    /// <summary>Finds the group owning a sound code by its 2-digit prefix, or null.</summary>
    public static SoundGroupDef? FindGroupForCode(string code)
    {
        if (code.Length < 2) return null;
        var prefix = code[..2];
        foreach (var g in Groups)
            if (g.Prefix == prefix) return g;
        return null;
    }

    // Reverse index: exact asset path → its catalog group + def. Covers clio.*.wav and the
    // explicitly-pathed families (tell alerts) uniformly, so playback gating need not parse codes.
    private static readonly Dictionary<string, (SoundGroupDef Group, SoundDef Def)> s_byAsset = BuildAssetIndex();

    private static Dictionary<string, (SoundGroupDef, SoundDef)> BuildAssetIndex()
    {
        var map = new Dictionary<string, (SoundGroupDef, SoundDef)>(StringComparer.Ordinal);
        foreach (var g in Groups)
            foreach (var s in g.Sounds)
                map[s.AssetName] = (g, s);
        return map;
    }

    /// <summary>The catalog group + def for an exact asset path, or null when not catalogued.</summary>
    public static (SoundGroupDef Group, SoundDef Def)? FindByAsset(string assetName)
        => s_byAsset.TryGetValue(assetName, out var hit) ? hit : null;
}
