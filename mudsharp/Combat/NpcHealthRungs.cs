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
/// against 4 to a better one, and not one transition contradicting the order below. That mattered - an
/// earlier hand-written draft placed "covered in wounds" BELOW "seriously injured", which would have
/// drawn a creature two rungs healthier than it was, in the direction that gets a character killed.</para>
///
/// <para><b>No published source corroborates this.</b> The MUD2 strategy guide documents damage
/// formulas, per-creature stamina pools and flee costs (see tools/combat/MUD2-PUBLISHED-MECHANICS.md)
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
    /// <para><b>It does NOT keep all object condition out, despite what this used to claim.</b> The
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
        ["full of energy"] = 7,
        // 6 - a scratch.
        ["superficially injured"] = 6,
        ["superficially damaged"] = 6,
        // 5.
        ["to have minor injuries"] = 5,
        ["to have minor damage"] = 5,
        // The banshee's rung-5 word, in the slot the other families fill with "minor". It sat at 6
        // until 2026-09-04 on the reasoning that it was "only seen at full or near-full health" -
        // which was reading the word rather than the corpus. Across seven banshee fights the step
        // superficially damaged -> slightly weakened happens five times and never reverses, and two
        // phrases genuinely sharing a rung do not produce a one-way progression.
        ["slightly weakened"] = 5,
        // 4 - the rung where the vocabularies diverge: living creatures say "covered in wounds" here
        // and never "moderately"; undead say "moderately damaged" and never "wounds".
        ["covered in wounds"] = 4,
        ["moderately damaged"] = 4,
        ["moderately drained"] = 4,
        // "moderately injured" was here and is gone for the same reason "critically drained" is:
        // zero occurrences corpus-wide, invented by analogy. Living creatures say "covered in
        // wounds" at 4 and never "moderately", which the line above already states. Keeping it gave
        // the living vocabulary eight words and quietly contradicted the seven-words-per-family rule
        // the rest of this table rests on. Both deletions are behavioural no-ops - the severity
        // fallback reads "moderately" as 4 and "critically" as 2 regardless.
        // 3.
        ["seriously injured"] = 3,
        ["seriously damaged"] = 3,
        ["seriously drained"] = 3,
        // 2.
        ["critically injured"] = 2,
        ["critically damaged"] = 2,
        // The banshee's rung-2 word, where the other families say "critically". Was 1 until
        // 2026-09-04. "critically drained" used to sit here instead and has been deleted: it occurs
        // ZERO times in the corpus and was an entry invented by analogy with the other two families,
        // which is how the banshee ended up with no rung-2 word and two words at 1.
        ["to be fading rapidly"] = 2,
        // 1 - one more hit. Never 0: a creature that reads at all is still standing.
        ["close to death"] = 1,
        ["close to expiry"] = 1,
        // The banshee's terminal reading, and the only descriptor in the corpus with no severity
        // adverb - so it missed this table AND fell straight through the BySeverity fallback, and
        // TryParse returned false. The cost: of the 3,484 non-killing player hits in clogs whose
        // fight began after 2026-08-16 (the window matters - NpcHealth only emits from 2026-08-10,
        // so an unwindowed count is 1,291 and meaningless), 27 have no descriptor before the next
        // event naming that creature, and all 27 are banshee. Widening to 2026-08-11 gives 34 of
        // 4,210, still 100% banshee. In the five fights whose raw wire is readable, every silent
        // hit is exactly a "faint" line.
        //
        // It IS a health reading, and that is settled by the protocol rather than by the wording:
        // the line arrives under FE code 12 (0xA7), byte-identical to every other descriptor and to
        // `ql` output. A spirit turning see-through would be a visibility event, and visibility has
        // its own codes (04 00 04/05) which are binary with no graded state - this is not one.
        //
        // Rung 1 because it is TERMINAL: where it appears it is always the last reading, and a hit
        // kills shortly after. It is not in every banshee fight - the 2026-09-04 one died out of
        // "to be fading rapidly" without ever printing it - so absence says nothing. The "never 0"
        // rule above sets the floor.
        //
        // The banshee's seven words map 1:1 onto 7..1, which is what every other vocabulary does and
        // is why this one now reads strong / superficially damaged / slightly weakened / moderately
        // drained / seriously drained / to be fading rapidly / faint. Settled 2026-09-04 on seven
        // fights whose raw wire is readable, and confirmed on all 45 banshee fight-segments in the
        // clogs: scoring every word-change, the old table gave 120 strict descents and 27 FLAT steps
        // - the game saying the creature had changed while the rail showed the same rung - against
        // 147 strict and ZERO flat under this one. One regen either way. All three words that moved
        // are banshee-exclusive, so nothing else shifted.
        //
        // Independently, and on a different axis: summing bracket midpoints per fight puts the seven
        // words on an even staircase of ~11.7 damage per rung against a pool of ~82 (published STA
        // 80). The four undisputed words land on their existing rungs, which validates the method,
        // and both disputed words land on the NEW value, not the old.
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
        // ever seen is the banshee's "slightly weakened", which the 2026-09-04 remap put at 5. A
        // fallback that generalises the value the table just disproved is worse than no fallback.
        ("slightly", 5),
        ("minor", 5),
        ("moderately", 4),
        ("seriously", 3),
        ("critically", 2),
        ("close to", 1),
        // Also 2, for the same reason "slightly" is 5: the sole observed "fading" phrase is the
        // banshee's, and it is rung 2 as of the remap.
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
}
