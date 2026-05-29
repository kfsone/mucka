using System.Text.RegularExpressions;
using MudSharp.Models;

namespace MudSharp.Protocol;

/// <summary>
/// Scans completed game lines for embedded stat tokens, mirroring Clio's scan_game_line().
/// </summary>
internal sealed class GameLineAnalyzer
{
    // "stamina:        81      max:    81"
    private static readonly Regex StaminaMaxRegex = new(
        @"^stamina:\s*(\d+)\s+max:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "Your stamina is 81."  (wake-up / rest line)
    private static readonly Regex YourStaminaRegex = new(
        @"^Your stamina is (\d+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "(150/200)"  compact stamina at the start of a line
    private static readonly Regex CompactStaminaRegex = new(
        @"^\((\d+)/(\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "The rat hits you (89/94)."  — stamina embedded in combat hit lines; find the last (N/M)
    private static readonly Regex CombatStaminaRegex = new(
        @"\((\d+)/(\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "strength:       94" or "strength:       94      effective strength:     47"
    private static readonly Regex StrengthRegex = new(
        @"^strength:\s*(\d+)(?:.*?effective strength:\s*(\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "dexterity:      95" or "dexterity:      95      effective dexterity:    84"
    private static readonly Regex DexterityRegex = new(
        @"^dexterity:\s*(\d+)(?:.*?effective dexterity:\s*(\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "score:  1,785 points    this game: ..."
    private static readonly Regex ScoreRegex = new(
        @"^score:\s*([\d,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "(Persona saved on [+N = ]M,NNN)."  — the last comma-separated number before ').'
    private static readonly Regex PersonaSavedScoreRegex = new(
        @"\(Persona saved on .*?([\d,]+)\)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // `passes you a note which says "troulm"` or `gasps "orchid"` etc.
    // Only matched outside game mode (pre-login); in game mode dreamwords arrive
    // exclusively via the binary C15+C00+C00+C255 sequence in Mud2C1Decoder.
    // "says" is intentionally excluded — it is a normal player speech verb and
    // produces false positives (e.g. 'Ollie says "boom"').
    private static readonly Regex DreamwordLineRegex = new(
        @"(?:gasps|whispers|shouts|screams|hisses|murmurs)\s+""([a-z]{1,14})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Analyse a completed line for embedded stat tokens.
    /// Returns a snapshot containing any found stats, or null if no stats were found.
    /// </summary>
    /// <param name="line">The styled line to analyse.</param>
    /// <param name="inGameMode">
    /// When true, the dreamword regex is skipped. In game mode dreamwords arrive exclusively
    /// via the binary C15+C00+C00+C255 sequence; the text regex only applies pre-game.
    /// </param>
    internal GameStatsSnapshot? Analyze(StyledLine line, bool inGameMode = false)
    {
        var text = line.PlainText;
        if (text.Length == 0)
            return null;

        // "(Persona saved on ...)" — early return in Clio; also extracts score from the number
        if (text.Contains("(Persona saved on "))
        {
            var pm = PersonaSavedScoreRegex.Match(text);
            var score = pm.Success ? StripCommas(pm.Groups[1].Value) : 0;
            return score > 0
                ? GameStatsSnapshot.Empty with { PersonaSaved = true, Score = score }
                : GameStatsSnapshot.Empty with { PersonaSaved = true };
        }

        // "stamina: N  max: M"
        var m = StaminaMaxRegex.Match(text);
        if (m.Success)
            return GameStatsSnapshot.Empty with
            {
                Stamina    = int.Parse(m.Groups[1].Value),
                MaxStamina = int.Parse(m.Groups[2].Value),
            };

        // "strength: N [effective strength: M]"  — use effective when present, else raw
        m = StrengthRegex.Match(text);
        if (m.Success)
        {
            var raw       = int.Parse(m.Groups[1].Value);
            var effective = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : raw;
            return GameStatsSnapshot.Empty with { Strength = effective };
        }

        // "dexterity: N [effective dexterity: M]"  — use effective when present, else raw
        m = DexterityRegex.Match(text);
        if (m.Success)
        {
            var raw       = int.Parse(m.Groups[1].Value);
            var effective = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : raw;
            return GameStatsSnapshot.Empty with { Dexterity = effective };
        }

        // "score: N,NNN points ..."
        m = ScoreRegex.Match(text);
        if (m.Success)
        {
            var score = StripCommas(m.Groups[1].Value);
            if (score > 0)
                return GameStatsSnapshot.Empty with { Score = score };
        }

        // "Your stamina is N."
        m = YourStaminaRegex.Match(text);
        if (m.Success)
            return GameStatsSnapshot.Empty with { Stamina = int.Parse(m.Groups[1].Value) };

        // "(N/M)" compact stamina
        m = CompactStaminaRegex.Match(text);
        if (m.Success)
        {
            var sta  = int.Parse(m.Groups[1].Value);
            var msta = int.Parse(m.Groups[2].Value);
            if (msta > 0)
                return GameStatsSnapshot.Empty with { Stamina = sta, MaxStamina = msta };
        }

        // "(N/M)" embedded anywhere in line (combat hit messages e.g. "The rat hits you (89/94).")
        // Use the last match to handle rare lines with multiple parenthesised numbers.
        var combatMatches = CombatStaminaRegex.Matches(text);
        if (combatMatches.Count > 0)
        {
            var last = combatMatches[combatMatches.Count - 1];
            var sta  = int.Parse(last.Groups[1].Value);
            var msta = int.Parse(last.Groups[2].Value);
            if (sta > 0 && msta > 0 && sta <= msta)
                return GameStatsSnapshot.Empty with { Stamina = sta, MaxStamina = msta };
        }

        // `passes you a note which says "word"` — dreamword delivered as game text.
        // Only matched outside game mode; in game mode dreamwords arrive exclusively
        // via the binary C15+C00+C00+C255 sequence in Mud2C1Decoder.
        if (!inGameMode)
        {
            m = DreamwordLineRegex.Match(text);
            if (m.Success)
                return GameStatsSnapshot.Empty with { DreamWord = m.Groups[1].Value };
        }

        return null;
    }

    private static int StripCommas(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int len = 0;
        foreach (var c in s)
            if (c != ',') buf[len++] = c;
        return int.TryParse(buf[..len], out var val) ? val : 0;
    }

    // Text triggers mirror Clio's sound.c pattern matches (game mode only).
    internal string? CheckSoundTrigger(StyledLine line)
    {
        var text = line.PlainText;
        if (text.StartsWith("Out from the end of the cannon flies a projectile, which smashes", StringComparison.Ordinal))
            return "sounds/clio.1313.wav";
        if (text.StartsWith("HAWUMPH! The dragon incinerates you with its fiery breath.", StringComparison.Ordinal))
            return "sounds/clio.1325.wav";
        if (text.StartsWith("You hear a near-deafening crash, as if millions of gallons of water", StringComparison.Ordinal))
            return "sounds/clio.1326.wav";
        return null;
    }
}
