namespace Mucka.WireLog;

/// <summary>
/// What every pass that rewrites the wire log of ended runs - <c>WireHeal</c> at start-up, and the
/// Logs page's trims, unwraps, reverts, expiries and deletes - shares, so that they do not interleave
/// with each other.
/// </summary>
public static class WireMaintenance
{
    /// <summary>Held for the whole of a pass. A pass plans from a snapshot of a run's rows, and a second
    /// pass changing those rows in between would make the plan wrong. One process at a time; a second
    /// Mucka on the same file is covered by each pass checking what it changes.</summary>
    public static readonly object Gate = new();
}
