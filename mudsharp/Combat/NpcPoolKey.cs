namespace MudSharp.Combat;

/// <summary>
/// The bucket a creature's STAMINA POOL belongs to: the name with its instance number stripped, so
/// "large rat0" and "large rat7" share a key and neither shares one with "rat0".
///
/// <para><b>Why this is not <see cref="NpcGroups"/>.</b> That normalizer answers a different
/// question - it pluralizes the LAST token, so "large rat0", "rat0" and "giant cave bat" all collapse
/// to "rats"/"bats". That is the right key for weapon susceptibility (a rat is a rat as far as a
/// dagger is concerned) and the wrong one for a pool: a "large rat0" runs to roughly 100 stamina
/// where an ordinary numbered rat runs to roughly 25. Pooling those under one name produces a band
/// four times too wide, and one that is wrong at both ends.</para>
///
/// <para>Provenance of that 100/25 (audit, 2026-09-03): it is <c>tools/combat/bestiary.tsv</c>, rows
/// <c>Rat0 STA 100</c> and <c>Rat1-21 STA 25</c> - the published GameFAQs guide, which the repo
/// labels hypothesis, NOT a corpus measurement. The corpus corroborates the SEPARATION rather than
/// the values: mean total damage to kill is 102.9 over 27 "large rat0" fights against 28.9-33.3 for
/// each numbered rat, a clean 3.3x split (both read high by the overkill bias described in
/// StaminaPoolEstimator). Note the guide's 100-stamina row is named <c>Rat0</c> while the creature
/// the client sees is <c>large rat0</c>; matching those two is an assumption, and no bare
/// <c>rat0</c> appears in the corpus at all.</para>
///
/// <para><b>Why the instance number and nothing else is stripped.</b> The number is not believed to
/// carry pool information, and dropping it is what buys the sample size.
/// <para><b>The evidence for that was overstated and has been withdrawn (audit, 2026-09-03).</b>
/// This used to read "rat3 and rat7 are statistically indistinguishable (17 instances, p=0.80)". No
/// script, document or stored result in the repo produces that p-value, it named no test and no
/// variable, and "17 instances" matches nothing: every database snapshot holds 23 distinct rat
/// instance names, and the per-instance samples are rat3 = 27 fights / 112 player swings / 26 kills
/// and rat7 = 28 fights / 113 player swings / 28 kills. Nor would a high p have shown what it was
/// used for - failing to reject at n≈27 is not evidence of equivalence, and re-running the plausible
/// candidates today, the variable that matters most for a POOL (total damage to kill: rat3 33.31,
/// rat7 30.66) comes out at p≈0.08, the closest to significant of anything tested.</para>
/// <para>What the design actually rests on, which is enough on its own: MUD2 numbers instances of one
/// spawn class, the bestiary assigns stamina per class rather than per instance, and a per-instance
/// key would mark every unmet number of a species the player has killed twenty of as brand new -
/// a claim the evidence positively does not support. If a per-instance difference is ever wanted, it
/// needs a script under <c>tools/combat/</c> with its output stored beside it, not a p-value in a
/// comment.</para>
/// Everything
/// else in the name is KEPT, adjectives included. That is deliberately wider than "species plus the
/// size adjective": rather than hold a hand-written vocabulary of which words are size words (which
/// would be a guess, and would silently mis-bucket the first creature whose adjective was not on the
/// list), every adjective splits the bucket. The cost is a possible extra bucket for a non-size
/// adjective; the alternative cost is conflating two creatures with different pools, which is the
/// error that actually produces a wrong number.</para>
///
/// <para><b>Several physical creatures can still hide behind one key.</b> The unnumbered mobs - coot,
/// fox, piglet, raven, ram, billy goat, banshee, thief - are spawned without an instance number at
/// all, so every one of them shares a single name and this key cannot separate them. Nothing here
/// can fix that; only a wide band is honest for those, and <see cref="StaminaPoolEstimator"/> reports
/// the width rather than hiding it.</para>
/// </summary>
public static class NpcPoolKey
{
    /// <summary>
    /// The pool key for an instance name, lower-cased with the trailing instance digits removed and
    /// internal whitespace collapsed to single spaces. Empty for a blank or all-digits name, which
    /// every caller treats as "no key" rather than as a bucket.
    /// </summary>
    public static string For(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return string.Empty;

        var trimmed = npcName.Trim().ToLowerInvariant();

        var end = trimmed.Length;
        while (end > 0 && char.IsAsciiDigit(trimmed[end - 1]))
            end--;
        var baseName = trimmed[..end].TrimEnd();

        if (baseName.Length == 0)
            return string.Empty;

        // Collapse runs of whitespace so "large  rat" and "large rat" cannot become two buckets.
        // Hyphens are left exactly as the game prints them ("water-snake"), since they are part of
        // the creature's name rather than separators the client invented.
        var tokens = baseName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? string.Empty : string.Join(' ', tokens);
    }

    /// <summary>
    /// The last word of a pool key - the species word, with any adjectives dropped ("large rat" ->
    /// "rat"). Used only where a property is biological rather than per-variant, which so far is
    /// exactly one thing: whether the creature regenerates. Never used for bucketing measurements.
    /// </summary>
    public static string SpeciesWordOf(string poolKey)
    {
        if (string.IsNullOrEmpty(poolKey))
            return string.Empty;
        var index = poolKey.LastIndexOfAny([' ', '-']);
        return index < 0 ? poolKey : poolKey[(index + 1)..];
    }
}
