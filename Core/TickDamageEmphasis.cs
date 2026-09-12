namespace Mucka.Core;

/// <summary>
/// Whether a badge's name is drawn bold: the rule for "this thing took damage on the tick you are
/// looking at".
///
/// <para>A badge's name is drawn bold only during a tick where its subject took damage, excepting
/// damage that arrived within the last sixth of the tick (about 300 ms) before the boundary: an
/// early-arriving packet must not bold the badge for only a handful of milliseconds.</para>
///
/// <para><b>What "we" means per badge.</b> Each tile is the subject of its own row - the perspective
/// rule the rail is already built on, where an opponent's tile shows what IT takes above and what it
/// deals below. So a creature's name bolds when that creature took a blow, and the player's name
/// bolds when the player did. The alternative reading, "bold every name whenever the player is hit",
/// would light the whole panel at once and so tell you nothing about any single badge, which is the
/// opposite of what a per-badge cue is for.</para>
///
/// <para><b>Why the carry-forward exists.</b> MUD2 resolves combat on a 2000 ms tick and the client
/// sees a tick's frame arrive within a few tens of milliseconds of the boundary - but "within" cuts
/// both ways, and a frame can land just BEFORE the boundary it belongs to. Bolding only until the end
/// of the tick the arrival timestamp fell in would then give that badge a 50 ms flash: technically
/// correct, visually a glitch, and worse than not bolding at all because a cue you cannot catch reads
/// as a rendering fault. So damage arriving inside the last <see cref="CarryForwardMs"/> of a tick is
/// treated as the NEXT tick's, which is very probably where it belongs anyway.</para>
///
/// <para><b>Pure, and sampled rather than scheduled.</b> Nothing here holds state or starts a timer -
/// it is a function of the arrival instant, "now", and the session's tick lattice, evaluated wherever
/// the view is built. Same pattern as <see cref="TickStaminaLoss.FadeFactor"/> and
/// <c>Mucka.Rendering.Blink</c>, and the same reason: Invariant #1 forbids a UI-thread timer driving
/// a visual, and the client already refreshes the panel on every combat event plus a 1 Hz heartbeat.
/// That cadence can leave the bold up for up to a second past the boundary, which is the harmless
/// direction of the error - the rule guards against under-bolding, not over-bolding.</para>
///
/// <para>MAUI-free; linked into mudsharp.Tests.</para>
/// </summary>
public static class TickDamageEmphasis
{
    /// <summary>How much of a tick's tail is treated as belonging to the next tick. A sixth of 2000 ms
    /// is 333 ms, which is also the shortest the emphasis can ever be shown for - damage landing
    /// exactly on this boundary holds until the tick ends and no further.</summary>
    public const double CarryForwardMs = CombatTiming.TickMilliseconds / 6.0;

    /// <summary>
    /// True while a badge whose subject last took damage at <paramref name="damageUtc"/> should be
    /// drawn emphasised, as of <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="damageUtc">When the subject last took damage, or null if it never has - which is
    /// not emphasised, and is a different state from "took damage a long time ago".</param>
    /// <param name="tickAnchorUtc">A point on the session's tick lattice (Core.TickPhase.Anchor), or
    /// null before the estimate exists. Without it there is no boundary to align to, so the emphasis
    /// falls back to a rolling full tick from the arrival - which cannot under-bold, and is what the
    /// first couple of swings of a session get.</param>
    public static bool IsOn(DateTime? damageUtc, DateTime nowUtc, DateTime? tickAnchorUtc)
    {
        if (damageUtc is not DateTime at)
            return false;

        // Clamped rather than allowed negative: a reading timestamped marginally ahead of this refresh
        // is a clock artefact, not evidence that the blow has not landed yet. Same clamp
        // SidePanelViewModel applies to health ages.
        var elapsedMs = Math.Max(0.0, (nowUtc - at).TotalMilliseconds);
        return elapsedMs < HoldMs(at, tickAnchorUtc);
    }

    /// <summary>How long the emphasis lasts for damage that arrived at <paramref name="at"/> - the
    /// remainder of its own tick, or of the next one when it landed in the carry-forward tail.</summary>
    public static double HoldMs(DateTime at, DateTime? tickAnchorUtc)
    {
        if (tickAnchorUtc is not DateTime anchor)
            return CombatTiming.TickMilliseconds;

        // In (0, TickMilliseconds]: the distance from `at` to the end of the tick it fell in.
        var remaining = CombatTiming.MillisecondsToNextBoundary(anchor, at);
        return remaining < CarryForwardMs ? remaining + CombatTiming.TickMilliseconds : remaining;
    }
}
