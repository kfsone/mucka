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

    // "sex:            male"  — score sheet only; there is no FES field for it.
    private static readonly Regex SexRegex = new(
        @"^sex:\s*([A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "magic:          110"  — the score sheet reports current magic with no max (FES carries both).
    private static readonly Regex MagicRegex = new(
        @"^magic:\s*(\d+)(?:\s+max:\s*(\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "weight carried: 750g    max: 100kg"  /  "weight carried: nothing max:    100kg"
    // "nothing" is the server's word for an empty pack: a measurement of ZERO, not a missing
    // reading, and it is what most sheets say. The "max:" clause is optional so a server-wrapped
    // line still yields the carried figure (see tools/combat/TEXT-WRAPPING-REVIEW.md).
    private static readonly Regex WeightCarriedRegex = new(
        @"^weight carried:\s*(?:(?<nothing>nothing)|(?<carried>\d+)(?<cunit>kg|g))(?:\s+max:\s*(?<max>\d+)(?<munit>kg|g))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "objects carried:        1       max:    12"  ("max:" optional — the line can wrap)
    private static readonly Regex ObjectsCarriedRegex = new(
        @"^objects carried:\s*(?<n>\d+)(?:\s+max:\s*(?<max>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "level:  7       champion"
    private static readonly Regex LevelRegex = new(
        @"^level:\s*(?<n>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "games played:   18"
    private static readonly Regex GamesPlayedRegex = new(
        @"^games played:\s*(?<n>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "score:  51,574 points   this game:      10 points       value:  10,389 points"
    // Three separate figures on one ~70-column line, so at narrow widths the server wraps it and
    // the tail arrives on a continuation line ("points        value:  9,534 points"). Each figure
    // therefore gets its own regex: the score prefix anchors at column 0, while the other two
    // anchor either at column 0 or immediately after the previous figure's "points" — enough of a
    // guard to keep player chatter from matching, while surviving the wrap.
    private static readonly Regex ScoreRegex = new(
        @"^score:\s*([\d,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "this game:      10 points" — can be zero, and (points lost this game) can be negative.
    private static readonly Regex ThisGameRegex = new(
        @"(?:^|points\s+)this game:\s*(-?[\d,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "value:  10,389 points" — the persona's own value; what an attacker collects when we flee or
    // die (see tools/combat/MUD2-PUBLISHED-MECHANICS.md, "Where the points go"). The colon is what
    // keeps this off the `value <name>` sniff reply ("The value of Ollie the warlock is N points.").
    private static readonly Regex ValueRegex = new(
        @"(?:^|points\s+)value:\s*(-?[\d,]+)",
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

        // All numeric captures parse with TryParse: any player can put "(99999999999999/9)"
        // in a say/shout, and an int.Parse OverflowException here propagates out of Feed()
        // and tears down the connection.

        // "stamina: N  max: M"
        var m = StaminaMaxRegex.Match(text);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var staVal)
            && int.TryParse(m.Groups[2].Value, out var mstaVal))
            return GameStatsSnapshot.Empty with { Stamina = staVal, MaxStamina = mstaVal };

        // "sex: male"
        m = SexRegex.Match(text);
        if (m.Success)
            return GameStatsSnapshot.Empty with { Sex = m.Groups[1].Value.ToLowerInvariant() };

        // "strength: N [effective strength: M]"  — use effective when present, else raw.
        // The effective clause is printed ONLY when it differs from the base, so an absent group
        // means "equal", not "unknown": fall back to the raw value rather than leaving it null.
        // Effective may be HIGHER than raw (a buff: "strength: 100  effective strength: 105"), so
        // nothing here clamps or assumes a penalty.
        m = StrengthRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var strRaw))
        {
            var effective = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var strEff) ? strEff : strRaw;
            return GameStatsSnapshot.Empty with { RawStrength = strRaw, Strength = effective };
        }

        // "dexterity: N [effective dexterity: M]"  — use effective when present, else raw
        m = DexterityRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var dexRaw))
        {
            var effective = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var dexEff) ? dexEff : dexRaw;
            return GameStatsSnapshot.Empty with { RawDexterity = dexRaw, Dexterity = effective };
        }

        // "magic: N" — the score sheet's only magic reading (FES supplies current + max).
        m = MagicRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var magic))
        {
            int? maxMagic = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var mmag) ? mmag : null;
            return GameStatsSnapshot.Empty with { CurrentMagic = magic, MaxMagic = maxMagic };
        }

        // "weight carried: 750g max: 100kg" / "weight carried: nothing max: 100kg"
        m = WeightCarriedRegex.Match(text);
        if (m.Success)
        {
            int? carriedGrams = null;
            if (m.Groups["nothing"].Success)
                carriedGrams = 0;   // "nothing" is zero, and zero is a measurement
            else if (TryParseWeightGrams(m.Groups["carried"].Value, m.Groups["cunit"].Value, out var carried))
                carriedGrams = carried;

            int? maxGrams = m.Groups["max"].Success
                && TryParseWeightGrams(m.Groups["max"].Value, m.Groups["munit"].Value, out var max)
                ? max : null;

            if (carriedGrams is not null || maxGrams is not null)
                return GameStatsSnapshot.Empty with { WeightCarriedGrams = carriedGrams, MaxWeightGrams = maxGrams };
        }

        // "objects carried: N [max: M]"  — N is legitimately 0 (empty pack)
        m = ObjectsCarriedRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups["n"].Value, out var objectsCarried))
        {
            int? maxObjects = m.Groups["max"].Success && int.TryParse(m.Groups["max"].Value, out var mo) ? mo : null;
            return GameStatsSnapshot.Empty with { ObjectsCarried = objectsCarried, MaxObjectsCarried = maxObjects };
        }

        // "level: N ..."
        m = LevelRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups["n"].Value, out var level))
            return GameStatsSnapshot.Empty with { Level = level };

        // "games played: N"
        m = GamesPlayedRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups["n"].Value, out var gamesPlayed))
            return GameStatsSnapshot.Empty with { GamesPlayed = gamesPlayed };

        // "score: N,NNN points   this game: N points   value: N,NNN points" — three figures.
        // Score keeps its historical "only when > 0" guard (a bare 0 is not worth trusting from a
        // loose text match); this game / value are taken at face value, zero included.
        m = ScoreRegex.Match(text);
        if (m.Success)
        {
            int? score = TryStripCommas(m.Groups[1].Value, out var total) && total > 0 ? total : null;
            var (thisGame, value) = MatchScoreExtras(text);
            if (score is not null || thisGame is not null || value is not null)
                return GameStatsSnapshot.Empty with { Score = score, ScoreThisGame = thisGame, PlayerValue = value };
        }

        // Wrapped tail of the score line ("points        value:  9,534 points").
        if (text.Contains("value:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("this game:", StringComparison.OrdinalIgnoreCase))
        {
            var (thisGame, value) = MatchScoreExtras(text);
            if (thisGame is not null || value is not null)
                return GameStatsSnapshot.Empty with { ScoreThisGame = thisGame, PlayerValue = value };
        }

        // "Your stamina is N."
        m = YourStaminaRegex.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var yourSta))
            return GameStatsSnapshot.Empty with { Stamina = yourSta };

        // "(N/M)" compact stamina
        m = CompactStaminaRegex.Match(text);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var cSta)
            && int.TryParse(m.Groups[2].Value, out var cMsta)
            && cMsta > 0)
            return GameStatsSnapshot.Empty with { Stamina = cSta, MaxStamina = cMsta };

        // "(N/M)" embedded anywhere in line (combat hit messages e.g. "The rat hits you (89/94).")
        // Use the last match to handle rare lines with multiple parenthesised numbers.
        var combatMatches = CombatStaminaRegex.Matches(text);
        if (combatMatches.Count > 0)
        {
            var last = combatMatches[combatMatches.Count - 1];
            if (int.TryParse(last.Groups[1].Value, out var sta)
                && int.TryParse(last.Groups[2].Value, out var msta)
                && sta > 0 && msta > 0 && sta <= msta)
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

    /// <summary>The "this game" / "value" figures from a score line, either of which may be absent
    /// (wrapped onto the next line, or simply not printed).</summary>
    private static (int? ThisGame, int? Value) MatchScoreExtras(string text)
    {
        var tg = ThisGameRegex.Match(text);
        var va = ValueRegex.Match(text);
        int? thisGame = tg.Success && TryStripCommas(tg.Groups[1].Value, out var g) ? g : null;
        int? value    = va.Success && TryStripCommas(va.Groups[1].Value, out var v) ? v : null;
        return (thisGame, value);
    }

    private static bool TryStripCommas(string s, out int value)
    {
        Span<char> buf = stackalloc char[s.Length];
        int len = 0;
        foreach (var c in s)
            if (c != ',') buf[len++] = c;
        return int.TryParse(buf[..len], out value);
    }

    private static int StripCommas(string s) => TryStripCommas(s, out var val) ? val : 0;

    private static bool TryParseWeightGrams(string magnitudeText, string unitText, out int grams)
    {
        grams = 0;
        if (!int.TryParse(magnitudeText, out var magnitude))
            return false;
        var multiplier = unitText.Equals("kg", StringComparison.OrdinalIgnoreCase) ? 1000 : 1;
        long scaled = (long)magnitude * multiplier;
        if (scaled > int.MaxValue || scaled < int.MinValue)
            return false;
        grams = (int)scaled;
        return true;
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
