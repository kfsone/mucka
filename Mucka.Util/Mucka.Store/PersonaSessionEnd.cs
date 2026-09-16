namespace Mucka.Store;

/// <summary>
/// The vocabulary of <c>persona_sessions.ended_note</c>: how a login finished, as far as the client
/// could tell.
///
/// <para><b>Advisory, and never load-bearing.</b> A crash leaves both this and <c>ended_ms</c> empty,
/// and a deliberate logout timed just before a reset is indistinguishable from one unrelated to it.
/// The absence of a note is not evidence that none of these happened - see the column comment in
/// 0003_persona_sessions.sql. What it is good for is the opposite direction: when it says
/// <see cref="Died"/>, something did.</para>
/// </summary>
public static class PersonaSessionEnd
{
    /// <summary>The world reset - the server terminated and logged everyone out. Taken from the C06
    /// C06 landing, not from prose.</summary>
    public const string Reset = "reset";

    /// <summary>The player quit. MUD2 prints <c>Cheerio!</c> and only ever does so for a deliberate
    /// exit.</summary>
    public const string Quit = "quit";

    /// <summary>
    /// An ordinary death: points lost, transient bonuses gone, a relog needed - but the persona
    /// survives and is still in the list at the next login.
    ///
    /// <para><b>Detected by an end-of-game summary with no <c>Cheerio!</c>.</b> MUD2 prints
    /// "Overall, you scored/lost N points this game." on the way out whether you quit or died; the
    /// <c>Cheerio!</c> is what only a quit gets. Measured over the whole wire corpus: every such
    /// summary is paired with a <c>Cheerio!</c> except two, and those two are the operator's two
    /// known deaths (a drowning and a marsh-gas explosion). No false positives.</para>
    ///
    /// <para>The verb in that line is NOT the discriminator - "scored" and "lost" track whether the
    /// session netted a gain, and a quit prints either. That reading was tried and the corpus
    /// disproved it.</para>
    ///
    /// <para>There is no C1 code for death. The one code near a death narrative (C18) also precedes
    /// unrelated scripted events - a lever trap, a chute ride - across at least fourteen runs, so it
    /// marks "dramatic narrative", not dying. Prose is all there is, which is the documented fallback
    /// when no code exists rather than a departure from the rule.</para>
    /// </summary>
    public const string Died = "died";

    /// <summary>The persona was wiped - the touchstone, and whatever else MUD2 does this for. Taken
    /// from the server's own C08+C13 (MudSession.PersonaWiped), not from the "Not updating persona."
    /// line that accompanies it.</summary>
    public const string Permadeath = "permadeath";
}
