namespace MudSharp.Protocol;

/// <summary>
/// Semantic scopes opened by C1 colour codes. MUD2 brackets every piece of structured output in
/// a colour push: the code that starts a chat message, an FE-probe response, a room long
/// description or the prompt container pushes a colour frame, and the scope lasts exactly until
/// that frame unwinds (a bare FF FF pop, a C90 colour throw, or the C00 init_stack reset). The
/// colour stack in <see cref="Mud2C1Decoder"/> is therefore the single source of truth for
/// "where am I in the stream": each stack frame records the scopes it opened, scope lifetime IS
/// frame lifetime, and <see cref="MudStreamParser.OnC1ScopesClosed"/> runs the end-of-scope
/// actions when frames unwind.
///
/// To add a new stream scope:
///   1. add a flag here;
///   2. pass it as the <c>opens</c> argument of the decoder's Apply at the code's dispatch site
///      (or <c>MoveScopeToTop</c> when the decision comes after the push, like the prompt gate);
///   3. read it via a <c>C1.HasScope(...)</c> property on <see cref="MudStreamParser"/>;
///   4. put any end-of-scope action in <see cref="MudStreamParser.OnC1ScopesClosed"/>.
/// Do NOT add a standalone bool + depth-counter pair for new stream state — that pattern's
/// hand-maintained unwind comparisons ('&lt;' vs '&lt;=') caused repeated missed-close bugs
/// before this mechanism replaced them.
/// </summary>
[Flags]
internal enum C1Scope
{
    None = 0,

    /// <summary>C09 speaker message (shout/say/tell/emote) — drives <see cref="Models.LineKind.Chat"/>
    /// and the wrapped-continuation tag (<see cref="Models.StyledLine.ContinuesChat"/>).</summary>
    Chat = 1 << 0,

    /// <summary>C02.02 room long description; each line also fires LongDescLineReady.</summary>
    LongDesc = 1 << 1,

    /// <summary>C12+C08+C05 FE WHO response — display suppressed, names captured.</summary>
    FewResponse = 1 << 2,

    /// <summary>C12+C08+C03 FE INVENTORY response — item lines captured.</summary>
    FeiResponse = 1 << 3,

    /// <summary>C12+C08+C02 FE EXITS response — exit keywords captured.</summary>
    FexResponse = 1 << 4,

    /// <summary>C01 prompt container — the whole prompt is captured and shown/discarded atomically.</summary>
    Prompt = 1 << 5,

    /// <summary>
    /// C04.0x.01–05 — a creature's presence sentence (here / arriving / departing / becoming
    /// (in)visible). The text inside is prose, not a name ("An evil, black rat (rat17) bares its
    /// razor-sharp incisors at you."), and it is captured so the Here list can tell a creature from an
    /// object: FEI returns both as bare names in one undifferentiated list, and this code is the only
    /// place the game itself says which is which.
    ///
    /// <para>Not the WHO-list variants (04.0x.06–08), which name a creature in a list rather than
    /// place one in the room, and are already routed to <see cref="Models.LineKind"/>-neutral
    /// FewPlayerData capture.</para>
    /// </summary>
    CreatureText = 1 << 6,

    /// <summary>
    /// C03.xx — an object appearing in a list. Opened purely so <see cref="CreatureText"/> can
    /// EXCLUDE it: MUD2 nests the item code inside the creature's sentence when the creature is
    /// carrying something ("There is a shifty-looking thief lurking here. The thief is carrying
    /// {C03.03}the tin4{/}."), and the scope flags are ORed over the whole colour stack, so without
    /// this the carried item's name would be captured as part of the creature's description and could
    /// mark an identically-named object on the floor as a creature.
    /// </summary>
    ListedObject = 1 << 7,
}
