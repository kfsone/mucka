namespace Mucka.WireLog;

/// <summary>
/// What every pass that rewrites the wire log of ended runs - <c>WireHeal</c> and the
/// <c>$trimdb</c> passes - shares, so that they do not interleave with each other and do not starve
/// the client's own writer.
/// </summary>
public static class WireMaintenance
{
    /// <summary>Held for the whole of a pass. A pass plans from a snapshot of a run's rows, and a second
    /// pass changing those rows in between would make the plan wrong. One process at a time; a second
    /// Mucka on the same file is covered by each pass checking what it changes.</summary>
    public static readonly object Gate = new();

    /// <summary>MUST GO AWAY. Operator rule: <c>$trimdb</c> is a command verb only until its
    /// management screen exists; that screen will not run while a game is running, and these pauses
    /// and every <see cref="Pause"/> call go with that change.
    ///
    /// <para>How long the passes a player starts in a game (<c>$trimdb</c>) sleep between
    /// transactions, so this process's own writer, recording that game, can get the write lock. The
    /// writer faults the store when its commit cannot get it for the busy timeout. The start-up heal
    /// does not sleep.</para></summary>
    public static readonly TimeSpan PauseBetweenTransactions = TimeSpan.FromMilliseconds(150);

    /// <summary>MUST GO AWAY, with <see cref="PauseBetweenTransactions"/>. Sleeps it.</summary>
    public static void Pause() => Thread.Sleep(PauseBetweenTransactions);
}
