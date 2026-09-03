using System.Text.RegularExpressions;

namespace MudSharp.Models;

/// <summary>Which way an object moved, and across which boundary.</summary>
public enum InventoryChangeKind
{
    /// <summary>"X dropped." — out of the pack and onto the floor. Count down, weight down.</summary>
    Dropped,
    /// <summary>"X taken." — off the floor and into the pack. Count up, weight up.</summary>
    Taken,
    /// <summary>"X inserted in Y." — out of the pack and into a container. Count down; the weight
    /// depends on where Y is, which this line does not say (see the class remarks).</summary>
    Stowed,
    /// <summary>"X removed from Y." — out of a container and into the pack. Count up; same weight
    /// caveat as <see cref="Stowed"/>.</summary>
    Retrieved,
}

/// <summary>
/// The four server lines that mean the player's loadout just changed, and nothing else.
///
/// <para><b>One owner for the patterns.</b> Two callers need these: CombatTracker, which turns them
/// into <c>CombatEvent</c>s naming the object, and MudSession, which uses them to fire the in-combat
/// FES,FEI probe. They used to be a proxy in one place and a private regex in the other. Kept here,
/// in the model layer, so neither the session nor the combat layer has to depend on the other and
/// the wordings cannot drift apart - the same reason CombatTiming exists.</para>
///
/// <para><b>The wordings, and only the wordings that were observed.</b> Swept from the 36 session
/// recordings under %LOCALAPPDATA%\Temp\mucka (2026-09-02): "X dropped." (368), "X taken." (370),
/// "X inserted in Y." and "X removed from Y." (~70 each across a dozen containers). "You drop
/// everything you're carrying!" is deliberately absent - it is always followed by one "X dropped."
/// line per item, so it is already covered and matching it too would only double-count.</para>
///
/// <para><b>The container pair is not a drop and not a take.</b> Per the owner, a container decouples
/// the two burdens: an object inside one does not levy its own dexterity cost (which is keyed on item
/// COUNT) but you still pay its weight (which is what strength is keyed on). So
/// "Baton inserted in glass bottle6." changes the count without changing the weight - the cleanest
/// single-variable observation the game offers of the two burdens coming apart - but ONLY if the
/// bottle is in the pack rather than on the floor, and the line does not say which. That is why the
/// container is captured rather than discarded: it is resolvable after the fact against the carry
/// list in the clog's own "contents" rows, and it would not be resolvable from a line that had been
/// folded into "dropped".</para>
/// </summary>
public static class InventoryChangeLines
{
    // Item names observed: letters, digits, apostrophes and hyphens, in at most a few words -
    // "Axe0", "Well-maintained pick2", "Cache of farthings", "Glass bottle15" (longest seen: three
    // words). Anchored at both ends and leading-capitalised, the way the server prints an object at
    // the start of a sentence.
    //
    // The FOUR-WORD CAP is not cosmetic. Without it, an unbounded run of name characters swallows a
    // whole sentence and "The starfish is embroiled in combat and can't be dropped." - the refusal,
    // i.e. the one line that means nothing moved - parses as a drop of an object called "The
    // starfish is embroiled in combat and can't be". Checked against the capture corpus: the cap
    // costs exactly that one false positive out of 340 matching lines and loses no real one.
    private const string Item = @"[A-Z][A-Za-z0-9'-]*(?: [A-Za-z0-9'-]+){0,3}";
    private const string Container = @"[A-Za-z0-9'-]+(?: [A-Za-z0-9'-]+){0,3}";

    private static readonly Regex DroppedOrTaken = new(
        $@"^(?<item>{Item}) (?<verb>dropped|taken)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ContainerMove = new(
        $@"^(?<item>{Item}) (?<verb>inserted in|removed from) (?<container>{Container})\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when this line reports that an object entered or left the player's hands.</summary>
    public static bool IsChange(string? text) => TryParse(text, out _, out _, out _);

    /// <summary>
    /// Parses one of the four lines. <paramref name="container"/> is non-null only for
    /// <see cref="InventoryChangeKind.Stowed"/> and <see cref="InventoryChangeKind.Retrieved"/>, and
    /// is the container as the game named it ("glass bottle6") so it can be looked up in the carry
    /// list later.
    /// </summary>
    public static bool TryParse(string? text, out InventoryChangeKind kind, out string item, out string? container)
    {
        kind = default;
        item = string.Empty;
        container = null;
        if (string.IsNullOrEmpty(text))
            return false;

        var m = DroppedOrTaken.Match(text);
        if (m.Success)
        {
            kind = m.Groups["verb"].Value == "dropped" ? InventoryChangeKind.Dropped : InventoryChangeKind.Taken;
            item = m.Groups["item"].Value;
            return true;
        }

        m = ContainerMove.Match(text);
        if (!m.Success)
            return false;
        kind = m.Groups["verb"].Value == "inserted in" ? InventoryChangeKind.Stowed : InventoryChangeKind.Retrieved;
        item = m.Groups["item"].Value;
        container = m.Groups["container"].Value;
        return true;
    }
}
