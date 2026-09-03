namespace MudSharp.Models;

/// <summary>What the swords glyph beside a "Here" row is doing.</summary>
public enum HereSwords
{
    /// <summary>Not drawn. An object has nothing to attack.</summary>
    Hidden,

    /// <summary>Drawn grey: a creature nothing is currently fighting. Present rather than absent so
    /// the row always offers the same affordance in the same place - it is the future click-to-attack
    /// target, and a control that appears only once a fight has started could never begin one.</summary>
    Idle,

    /// <summary>Drawn red: a fight against this exact instance is open right now.</summary>
    Engaged,
}

/// <summary>The four-row truth table behind a "Here" row's appearance, kept out of the view model so
/// it can be checked without a MAUI runtime.</summary>
public static class HereRow
{
    /// <summary>Which swords state a row shows. Engagement without creature-hood is impossible by
    /// construction (a fight IS one of the two things that makes a row a creature), but if it ever
    /// arrived it would still show the icon - being in a fight with something is stronger evidence
    /// that it is alive than any description.</summary>
    public static HereSwords SwordsFor(bool isCreature, bool isEngaged)
        => isEngaged ? HereSwords.Engaged
         : isCreature ? HereSwords.Idle
         : HereSwords.Hidden;
}

/// <summary>
/// Which names in the FEI "here" list are alive.
///
/// <para><b>Why this has to exist.</b> FEI returns the room's contents as bare names on separate
/// lines with no type marker at all - Bartle's own spec calls them "portable objects", and the
/// captures bear out that creatures are among them: every recording on disk mixes rats, a raven, a
/// coot, a parrot and a dragonfly into the same pre-<c>========</c> block as keys, brands and vials,
/// in no separating order (one observed block reads rat21, rat19, key1, rat16, rat17, brand40). So
/// the list itself cannot answer the question, and no rule about the SHAPE of a name can either -
/// "bat0" is a club in one capture's inventory and "bats" are a creature group in the fight corpus.
/// </para>
///
/// <para><b>The evidence used instead is the game's own classification.</b> MUD2 brackets a
/// creature's presence sentence in C1 code 04 (creatures) and an object's in code 03 - "An evil,
/// black rat (rat17) bares its razor-sharp incisors at you." arrives inside 04.00.01. That is
/// Bartle's taxonomy, not an inference, and it is the strongest evidence available for this
/// question. The sentence is prose rather than a name, so a FEI entry is judged alive when the
/// creature-coded text for THIS ROOM contains it.</para>
///
/// <para><b>Scoped to the current room, deliberately.</b> The sentences are cleared on room entry.
/// A learned vocabulary of species would classify faster but would carry a false positive across
/// rooms the moment an object shared a creature's name, and marking an item as a live creature -
/// with a swords icon inviting an attack - is a worse failure than not marking a creature at all.
/// </para>
///
/// <para><b>What it therefore cannot do.</b> If the client attaches mid-room, or reconnects without
/// a room description, nothing has been seen and every entry reads as an object until the player
/// moves or looks. That is the honest state: the game has not said, so the client does not claim.
/// Combat is the second, independent source - anything currently being fought is a creature whatever
/// this index knows - and the caller unions the two.</para>
///
/// <para>Not thread-safe. The single owner (the side panel view model) touches it from the Feed
/// thread on observation and from the UI thread on lookup, and serializes both through its own
/// main-thread marshalling, exactly as it already does for the pending FEI buffers.</para>
/// </summary>
public sealed class RoomCreatures
{
    /// <summary>Sentences retained for one room. MUD2 rooms hold single-digit creature counts and each
    /// sentence is one short line; the cap only exists so a pathological stream cannot grow this
    /// without bound, and it discards the OLDEST, which is the one most likely to describe something
    /// that has since left.</summary>
    private const int MaxSentences = 32;

    private readonly List<string> _sentences = [];

    /// <summary>Forgets everything seen in the previous room. Call on room entry.</summary>
    public void Clear() => _sentences.Clear();

    /// <summary>Records one creature-presence sentence, lower-cased once here so every later lookup is
    /// a plain ordinal search rather than a culture-sensitive one.</summary>
    public void Observe(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return;
        if (_sentences.Count >= MaxSentences)
            _sentences.RemoveAt(0);
        _sentences.Add(sentence.ToLowerInvariant());
    }

    /// <summary>
    /// Whether the game has described <paramref name="name"/> as a creature in this room.
    ///
    /// <para>A whole-token match, so "rat" cannot match "ratchet" and "bat0" cannot match "bat01".
    /// Anything that is not a letter, a digit, a hyphen or an apostrophe counts as a boundary, which
    /// is what lets the parenthesised form MUD2 uses for numbered instances - "(rat17)" - match,
    /// alongside the bare form it uses for the unnumbered mobs ("a dark, bedraggled coot sits here")
    /// and the multi-word names ("billy goat"). A hyphenated name matches as a whole ("water-snake")
    /// while its species word alone does not, which is the same split NpcPoolKey makes.</para>
    /// </summary>
    public bool IsCreature(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var needle = name.Trim().ToLowerInvariant();
        foreach (var sentence in _sentences)
        {
            if (ContainsToken(sentence, needle))
                return true;
        }
        return false;
    }

    /// <summary>Ordinal whole-token search. Public and static so the boundary rule is testable on its
    /// own rather than only through a populated index.</summary>
    public static bool ContainsToken(string haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        var from = 0;
        while (true)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var before = at == 0 || !IsWordChar(haystack[at - 1]);
            var afterIndex = at + needle.Length;
            var after = afterIndex >= haystack.Length || !IsWordChar(haystack[afterIndex]);
            if (before && after)
                return true;
            from = at + 1;
        }
    }

    // A hyphen and an apostrophe ARE word characters. "water-snake" still matches (its own hyphen is
    // inside the needle, and the characters either side of the whole name are spaces), while a bare
    // "snake" no longer matches inside "water-snake" - which is the direction that matters, since
    // NpcPoolKey treats those as two different creatures. Same for "bat0" inside "bat0-shaped".
    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '\'';
}
