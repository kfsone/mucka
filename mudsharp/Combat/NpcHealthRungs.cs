using System.Text.RegularExpressions;

namespace MudSharp.Combat;

/// <summary>
/// MUD2's only report of how hurt a creature is: the line it prints after a landed blow,
/// "The rat12 looks seriously injured."
///
/// <para>This is the single most valuable free signal in combat and the game gives it in words only -
/// no numbers, no bar, no percentage - so the whole job here is turning those words into one ordinal
/// scale that a gauge can draw. <see cref="Rungs"/> of them, best to worst.</para>
///
/// <para><b>The ordering is measured, not assumed.</b> It comes from counting which phrase replaced
/// which, within single fights as segmented by the offline reducer: 62 transitions to a worse rung
/// against 4 to a better one, and not one transition contradicting the order below.</para>
///
/// <para><b>No published source corroborates this.</b> The MUD2 strategy guide documents damage
/// formulas, per-creature stamina pools and flee costs (see docs/MUD2-published-mechanics.md)
/// and says nothing whatever about the wound descriptions. This ladder is the best available reading
/// of observed behaviour, not documented fact.</para>
///
/// <para><b>Three vocabularies, one scale.</b> Living things are "injured", undead are "damaged", and
/// a banshee is "drained"; the words differ but the rungs line up, and each vocabulary simply omits
/// one of the middle words the others use ("covered in wounds" is living-only, "moderately damaged"
/// undead-only). Matching on the ADJECTIVE rather than on whole phrases is what lets a vocabulary
/// nobody has seen yet still land on the right rung.</para>
///
/// <para><b>What this ladder is not.</b> It is not a health percentage and must never be drawn as one:
/// seven words cannot resolve a pool that runs from 1 (a firefly) to 800 (the dragon), and rung 2 on a
/// 25-stamina rat is a different amount of trouble than rung 2 on a 100-stamina rat0. Nor is it a
/// ratchet - creatures regenerate. A zombie in the corpus oscillates between "strong" and
/// "superficially damaged" four times in one fight, and another climbs from "moderately damaged" back
/// to "minor damage". Improvements are rare - 26 of 2,269 changed transitions, 1.15% - but they are
/// NOT all one rung: 3->6, 1->4, 2->5 and 4->6 all occur, so nothing may assume a single step. The
/// reading to show is always the LATEST one, never the worst seen.</para>
/// </summary>
public static class NpcHealthRungs
{
    /// <summary>Rungs on the scale, worst (1) to unhurt (<see cref="Rungs"/>). Seven, because each of
    /// the game's vocabularies uses exactly seven words - which is also why the rail's ladder has
    /// seven pips.</summary>
    public const int Rungs = 7;

    /// <summary>Rung of an unhurt creature - "fit" / "strong".</summary>
    public const int Unhurt = Rungs;

    /// <summary>
    /// The line, e.g. "The large rat0 looks covered in wounds." Anchored at the start so a creature
    /// name can never absorb surrounding prose. Only "looks" is accepted: it is the only verb observed
    /// in this line, and widening it would start matching object condition ("The coracle looks to be
    /// in relatively good condition.") and NPC aggro poses ("The rat looks at you furiously.").
    ///
    /// <para>The descriptor may end at a full stop OR run on after a comma: MUD2 sometimes folds the
    /// reading into a longer sentence - "The ram looks covered in wounds, and is holding the
    /// following:" - and an end-anchored pattern silently dropped a perfectly good rung-4 reading
    /// every time it did. The descriptor match is lazy so the run-on clause is never absorbed into it,
    /// and it still has to survive <see cref="TryRung"/>, which is what actually keeps aggro poses out.
    ///
    /// <para><b>It does NOT keep all object condition out.</b> The
    /// "in ... condition" family is rejected, but the BySeverity fallback matches an object's wear the
    /// same way it matches a creature's: "The broadsword looks to be seriously damaged." reads 3, the
    /// well-maintained pick reads 6, "The rolling-pin1 looks close to disintegration." reads 1 - eight
    /// such lines in the raw corpus. What actually contains this is the caller: CombatTracker only
    /// consults a reading for a name already in its active set, so an object has to share an engaged
    /// creature's name to land. Low risk, not no risk, and worth knowing before anything else starts
    /// calling TryParse.</para>
    /// </summary>
    private static readonly Regex Line = new(
        @"^The (?<npc>.+?) looks (?<desc>[a-z][a-z ]*?)(?:\.|,\s.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Phrase to rung. Keyed on the descriptor exactly as the game prints it (minus "The X looks "
    /// and the full stop), so every entry here is a string observed in a real capture.
    /// </summary>
    private static readonly Dictionary<string, int> ByPhrase = new(StringComparer.OrdinalIgnoreCase)
    {
        // 7 - unhurt.
        ["fit"] = 7,
        ["strong"] = 7,
        // The AT-MAX words. Both sit at 7 because that is the top BAND and nothing downstream can
        // hold an eighth value - see IsAtMax for what they actually mean and why the distinction is
        // exposed separately rather than as rung 8.
        ["full of energy"] = 7,
        ["full of life"] = 7,
        // 6 - a scratch.
        ["superficially injured"] = 6,
        ["superficially damaged"] = 6,
        // 5.
        ["to have minor injuries"] = 5,
        ["to have minor damage"] = 5,
        // The banshee's rung-5 word, in the slot the other families fill with "minor". Across seven
        // banshee fights the step superficially damaged -> slightly weakened happens five times and
        // never reverses, which places it here rather than at 6.
        ["slightly weakened"] = 5,
        // 4 - the rung where the vocabularies diverge: living creatures say "covered in wounds" here
        // and never "moderately"; undead say "moderately damaged" and never "wounds".
        ["covered in wounds"] = 4,
        ["moderately damaged"] = 4,
        ["moderately drained"] = 4,
        // 3.
        ["seriously injured"] = 3,
        ["seriously damaged"] = 3,
        ["seriously drained"] = 3,
        // 2.
        ["critically injured"] = 2,
        ["critically damaged"] = 2,
        // The banshee's rung-2 word, where the other families say "critically".
        ["to be fading rapidly"] = 2,
        // 1 - one more hit. Never 0: a creature that reads at all is still standing.
        ["close to death"] = 1,
        ["close to expiry"] = 1,
        // The banshee's terminal reading, and the only descriptor in the corpus with no severity
        // adverb - so it misses the BySeverity fallback below and needs an explicit ByPhrase entry.
        // Measured before this entry existed: of 3,484 non-killing player hits in clogs from fights
        // begun after 2026-08-16, 27 had no descriptor before the next event naming that creature,
        // and all 27 were banshee (34 of 4,210 from 2026-08-11, still all banshee).
        //
        // It IS a health reading, settled by protocol rather than by wording: the line arrives under
        // FE code 12 (0xA7), byte-identical to every other descriptor and to `ql` output. A spirit
        // turning see-through would be a visibility event, and visibility has its own codes (04 00
        // 04/05) which are binary with no graded state - this is not one.
        //
        // Rung 1 because it is TERMINAL: where it appears it is always the last reading, and a hit
        // kills shortly after. It is not in every banshee fight - one banshee died out of "to be
        // fading rapidly" without ever printing it - so absence says nothing. The "never 0" rule
        // above sets the floor.
        //
        // The banshee's seven COMBAT-DESCRIPTOR words map 1:1 onto 7..1, which is what every other
        // vocabulary does: strong / superficially damaged / slightly weakened / moderately drained /
        // seriously drained / to be fading rapidly / faint.
        //
        // "Combat-descriptor" is load-bearing: the inspection commands phrase rung 7 differently.
        // `ql banshee` on a healed banshee returned "full of energy", and the only other sighting of
        // that phrase in the corpus is a zombie4 EXAMINE - never a descriptor line, where the same
        // creatures say "strong". Both are rung 7 and both are in this table, so nothing is
        // mis-scored; but do not count words across surfaces and conclude a species has eight.
        //
        // Scoring every word-change across all 45 banshee fight-segments in the clogs gives 147
        // strict descents and zero flat steps (a flat step is the game saying the creature had
        // changed while the rail showed the same rung). All three banshee-specific words are
        // banshee-exclusive, so nothing else is affected.
        //
        // Independently, and on a different axis: summing bracket midpoints per fight puts the seven
        // words on an even staircase of ~11.7 damage per rung against a pool of ~82 (published STA
        // 80).
        //
        // Same-rung pairs never co-occur, which is what makes a flat step evidence of an error
        // rather than of two phrasings: across 1,315 clogs and 2,275 word-changes, no fight contains
        // both of fit/strong, close to death/close to expiry, or superficially injured/superficially
        // damaged. The duplicates in this table are cross-vocabulary, never within one creature.
        ["faint"] = 1,
    };

    /// <summary>
    /// Adjective to rung, for a phrase the table above has never seen. MUD2 clearly builds these
    /// lines as "&lt;severity&gt; &lt;damage-noun&gt;" per creature family, so a family nobody has
    /// fought yet ("moderately corroded", say) still lands correctly off its severity word alone.
    /// Without this, an unknown vocabulary would silently read as "no information" for a whole
    /// species.
    /// </summary>
    private static readonly (string Word, int Rung)[] BySeverity =
    [
        ("superficially", 6),
        // 5, not 6, and it must agree with ByPhrase above: the only "slightly" phrase anyone has
        // ever seen is the banshee's "slightly weakened", which ByPhrase puts at 5. A fallback that
        // generalises the value the table just disproved is worse than no fallback.
        ("slightly", 5),
        ("minor", 5),
        ("moderately", 4),
        ("seriously", 3),
        ("critically", 2),
        ("close to", 1),
        // Also 2, for the same reason "slightly" is 5: the sole observed "fading" phrase is the
        // banshee's, and it is rung 2 in ByPhrase.
        ("fading", 2),
    ];

    /// <summary>
    /// Parses a health-descriptor line. Returns false for anything else, including the lines that
    /// look deceptively similar - aggro poses and object condition.
    /// </summary>
    /// <param name="npcName">The creature as named, e.g. "large rat0".</param>
    /// <param name="rung">1 (about to die) to <see cref="Rungs"/> (unhurt).</param>
    /// <param name="phrase">The descriptor exactly as the game worded it, for echoing back to the
    /// player - reading their own game's words is what ties the panel to the scroll.</param>
    public static bool TryParse(string line, out string npcName, out int rung, out string phrase)
    {
        npcName = string.Empty;
        rung = 0;
        phrase = string.Empty;

        var match = Line.Match(line);
        if (!match.Success)
            return false;

        var desc = match.Groups["desc"].Value;
        if (!TryRung(desc, out rung))
            return false;

        npcName = match.Groups["npc"].Value;
        phrase = desc;
        return true;
    }

    /// <summary>The rung for a descriptor phrase on its own (no "The X looks " wrapper), by exact
    /// match first and by severity word second.</summary>
    public static bool TryRung(string descriptor, out int rung)
    {
        if (IsObjectCondition(descriptor))
        {
            rung = 0;
            return false;
        }

        if (ByPhrase.TryGetValue(descriptor, out rung))
            return true;

        foreach (var (word, value) in BySeverity)
        {
            if (descriptor.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                rung = value;
                return true;
            }
        }

        rung = 0;
        return false;
    }

    /// <summary>
    /// An object's wear, not a creature's health. `ql` on a carried item returns its own ladder and
    /// it shares the "The X looks ..." shape exactly, so the two are indistinguishable by shape.
    ///
    /// <para>Rejected here rather than left to the caller because <see cref="BySeverity"/> reads it
    /// as health and gets it badly wrong: "The rolling-pin1 looks close to disintegration." matches
    /// "close to" and returns rung 1, i.e. about to die. Observed in the corpus, and reachable in
    /// play - `ql` your own weapon while engaged with a creature whose name it shares and a bogus
    /// reading lands on the rail. "The broadsword looks to be seriously damaged." (rung 3) and the
    /// well-maintained pick at 6 are the same failure without the drama.
    ///
    /// <para>Two families, both from `ql`/examine on an item: the "in ... condition" ladder
    /// (excellent / very good / good / relatively good / poor / very bad) and the terminal
    /// "close to disintegration". The condition family was already rejected incidentally by not
    /// being in either table; it is stated explicitly now so that adding a "condition" phrase to
    /// ByPhrase later cannot silently re-open the hole.</para>
    /// </summary>
    /// <summary>
    /// The descriptor means <b>exactly</b> <c>cur == max</c> - untouched - rather than the top band.
    ///
    /// <para><b>Measured, not assumed.</b> 328 paired readings across three personae and six
    /// different maxima (61, 75, 85, 95, 97, 100), each pinning a descriptor against a stamina
    /// figure from the same frame. The biconditional holds both ways: <c>full of life</c> is never
    /// below max (n=15), and every single reading where cur == max returns <c>full of life</c> and
    /// nothing else (n=15, across four maxima). <c>fit</c> occurs 46 times and is never at max,
    /// topping out at 99/100, 71/75, 89/97. The game agrees at the protocol level - the C1 colour
    /// code on the stamina value is <c>99.10</c> only and exactly at max. <c>full of life</c> is the
    /// living word, <c>full of energy</c> the undead/spirit one, and neither has ever appeared in a
    /// combat descriptor line, because combat only describes a creature after damage, when
    /// cur &lt; max by construction. They come from <c>ql</c> and examine.</para>
    ///
    /// <para><b>Why this is not rung 8.</b> The ladder is <c>ceil(cur * 7 / max)</c> - fitted
    /// 328/328, against 223/328 for a hard-coded /100 - and that is seven BANDS. At-max is a point,
    /// not a band, so making <see cref="Rungs"/> 8 would corrupt every division that uses it
    /// (<c>DamagePrediction.StepsWide</c>) and every range check written as 1..Rungs
    /// (<c>PoolObservationBuilder</c>, which would silently drop an 8). Seven bands plus a point is
    /// the shape of the thing; the rail can draw it however it likes.</para>
    ///
    /// <para>Worth more than a rung to anything estimating a pool: a rung is a 1/7 band, this is an
    /// exact equality.</para>
    ///
    /// <para><b>A rung is a FRACTION, not a quantity.</b> "full of life" means 100% - not 100
    /// stamina, not 5, not 500. "close to death" means the bottom seventh of whatever that creature
    /// has. So the ladder needs no per-creature calibration and carries no absolute information: it
    /// applies to a rat and to a giant identically, and players are creatures like any other, which
    /// is why 328 player self-inspections measure the scale for everything.
    ///
    /// <para>What a creature's rung does NOT give you is its stamina. That is the pool-estimation
    /// problem and it is separate - see StaminaPoolEstimator. The stethoscope is the tool there:
    /// <c>diagnose</c> prints "has a stamina lying between X and Y" (CombatTracker's
    /// NpcStaminaRead), which is a numeric read on a creature, and combined with its rung it
    /// brackets the creature's max. npc_stamina_reads has zero rows - nobody has run it.</para>
    /// </summary>
    public static bool IsAtMax(string phrase)
        => phrase.Equals("full of life", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("full of energy", StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectCondition(string descriptor)
        => descriptor.Contains("condition", StringComparison.OrdinalIgnoreCase)
        || descriptor.Contains("disintegration", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the game's grammatical filler so a descriptor reads as a label. "to have minor
    /// injuries" -> "minor injuries", "to be fading rapidly" -> "fading rapidly"; everything else is
    /// already a label and passes through untouched.
    /// </summary>
    public static string Label(string phrase)
    {
        const string toHave = "to have ";
        const string toBe = "to be ";
        if (phrase.StartsWith(toHave, StringComparison.OrdinalIgnoreCase))
            return phrase[toHave.Length..];
        if (phrase.StartsWith(toBe, StringComparison.OrdinalIgnoreCase))
            return phrase[toBe.Length..];
        return phrase;
    }

    /// <summary>
    /// Which rung a known cur/max lands on: <c>ceil(cur * 7 / max)</c>. Null when the inputs cannot
    /// support an answer.
    ///
    /// <para><b>Provenance, honestly.</b> Treat the ladder formula as a reasonable inference, not as
    /// a measurement someone took. It is used here only for the player, whose maximum the game
    /// states outright, and only to choose a WORD; nothing decides anything on it.</para>
    ///
    /// <para><b>This is only ever asked about the PLAYER.</b> A creature's rung comes from the word
    /// MUD2 printed, never from arithmetic, because the client has no honest denominator for one - the
    /// pool estimate is an inference and putting it through this would dress it as a reading. The
    /// player is the single creature whose maximum the game states outright.</para>
    /// </summary>
    public static int? RungFor(int? current, int? max)
    {
        if (current is not int cur || max is not int maximum || maximum <= 0)
            return null;

        var clamped = Math.Clamp(cur, 0, maximum);
        var rung = (int)Math.Ceiling(clamped * (double)Rungs / maximum);
        return Math.Clamp(rung, 1, Rungs);
    }

    /// <summary>
    /// The LIVING family's word for a rung - fit / superficially injured / minor injuries / covered in
    /// wounds / seriously injured / critically injured / close to death.
    ///
    /// <para>Every one of these is lifted from <see cref="ByPhrase"/> rather than composed here, so
    /// this cannot drift into vocabulary MUD2 does not use. That matters: the undead say "damaged"
    /// where the living say "injured" and the banshee says neither, and a family invented by analogy
    /// is exactly how "critically drained" and "moderately injured" got into that table and had to be
    /// deleted again.</para>
    ///
    /// <para>Used for the player's own readout, the player being a living creature. The game's word
    /// for the player is available from <c>ql me</c> and is NOT captured today; when it is, prefer it
    /// over this, the same way every creature's own printed descriptor already outranks inference.</para>
    ///
    /// <para><b>Rung 7 is the shakiest entry and it is the resting state.</b> <see cref="IsAtMax"/>
    /// claims the game says "full of life" at exactly max and never "fit" - which would make the word
    /// this returns wrong for a player sitting at full stamina, the commonest thing the panel ever
    /// shows. "Full of life" appears ten times corpus-wide and every one describes an NPC, so there
    /// is no player evidence either way; the word stays because it cannot be shown wrong, not
    /// because it has been shown right.</para>
    ///
    /// <para>If an at-max distinction is wanted, the evidence for one already exists and is not a
    /// descriptor: the C1 code on the stamina numerator is <c>99.10</c> if and only if
    /// <c>cur == max</c> - 45 of 45 across 660 captured "hits you (cur/max)" frames, never once below
    /// max. Build on that rather than on a word nobody has captured for the player.</para>
    /// </summary>
    public static string? LivingLabel(int? rung) => rung switch
    {
        7 => "fit",
        6 => "superficially injured",
        5 => "minor injuries",
        4 => "covered in wounds",
        3 => "seriously injured",
        2 => "critically injured",
        1 => "close to death",
        _ => null,
    };
}
