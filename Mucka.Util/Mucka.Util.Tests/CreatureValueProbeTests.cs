using System.Text;
using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Session;
using MudSharp.Tests.Fixtures;   // VirtualSessionClock, linked from mudsharp.Tests where it lives
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The in-combat creature-value probe: `value &lt;name&gt;` reports the points awarded for killing
/// a creature. Learned once per newly-active roster name, batched across whatever joined since the
/// last quiet period ("value x and y and z" costs ONE server tick), and swallowed from the
/// terminal the same way the player-presence sniff already is - see MudSession's "In-combat
/// creature value probe" remarks for the design this exercises.
///
/// <para><b>Wire shape, and why every case below leads a frame with its prompt.</b> On real wire
/// traffic every server frame is LED by an IsPartial '*' prompt (verified: PostSelectSetupTests'
/// own model, taken from a live capture) - so a `value` command's echo and its reply each arrive
/// in a frame that STARTS with a prompt, not one that ends with one. The in-flight window must not
/// close on the first prompt seen after arming, since that would close it before the echo, let
/// alone the reply, on every realistic shape. Instead it tracks which requested names are still
/// unaccounted-for and only closes the window at the frame boundary that FOLLOWS the point where
/// all of them have drawn a reply or bad-target rejection - see TryConsumeCreatureValueLine's own
/// remarks. The cases below exercise the shapes this matrix names.</para>
/// </summary>
public class CreatureValueProbeTests : IDisposable
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // One frame-leading '*' prompt (IsPartial), verbatim from PostSelectSetupTests - a `value`
    // command's own frame starts here, exactly like any other typed command's.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly VirtualSessionClock _clock = new();
    private readonly List<string> _outgoing = new();
    private readonly List<(string Name, int Value)> _resolved = new();
    private readonly List<string> _visible = new();
    private readonly object _lock = new();

    public CreatureValueProbeTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromSeconds(60),   // far enough away not to interfere
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),   // shared debounce, see MudSession
        });
        _clock.Attach(_session);
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
        _session.CreatureValueResolved += (name, value) => { lock (_lock) _resolved.Add((name, value)); };
        _session.LineReady += l => { if (!l.IsPartial) lock (_lock) _visible.Add(l.PlainText); };
    }

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);

    private List<string> Sent()
    {
        lock (_lock) return _outgoing.ToList();
    }

    private List<(string Name, int Value)> Resolved()
    {
        lock (_lock) return _resolved.ToList();
    }

    private List<string> Visible()
    {
        lock (_lock) return _visible.ToList();
    }

    /// <summary>
    /// Step <paramref name="clock"/> forward in small slices, up to <paramref name="budget"/>,
    /// stopping the moment the condition holds. Nothing here waits on real time: a false result
    /// means the probe never fired within that much VIRTUAL time, which a loaded machine cannot
    /// change. The default budget is generous next to the debounce and the probe timeout, since
    /// what it bounds is the deadline arithmetic rather than a machine's spare capacity.
    /// </summary>
    private static bool Settled(VirtualSessionClock clock, Func<bool> condition, TimeSpan? budget = null)
    {
        var step = TimeSpan.FromMilliseconds(20);
        var left = budget ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            if (condition()) return true;
            if (left <= TimeSpan.Zero) return false;
            var by = left < step ? left : step;
            clock.Advance(by);
            left -= by;
        }
    }

    /// <summary>Enter game mode and open a fight against one named creature (a fresh
    /// PlayerAttackStart line, so CombatTracker.Begin fires ParticipantJoined for it).</summary>
    private void EnterCombat(string npc, string weapon = "axe0")
    {
        _session.Feed(GameModeEntry);
        Assert.True(_session.InGameMode);
        Feed($"You attack the {npc}, using the {weapon} as a weapon.\r\n");
        Assert.True(_session.InCombat);
        lock (_lock) { _outgoing.Clear(); _visible.Clear(); }
    }

    [Fact]
    public void FightStart_SendsAValueProbeForTheNewParticipant()
    {
        EnterCombat("rat17");
        Assert.True(Settled(_clock, () => Sent().Contains("value rat17\r\n")));
    }

    // -- Matrix row: "prompt, echo" / "prompt, reply" - the documented shape --------------------
    // (MudSession.TryConsumeCreatureValueLine's own remarks, and PostSelectSetupTests' model): echo and reply each arrive
    // as their OWN frame, each led by its own prompt.

    [Fact]
    public void ThousandsSeparator_IsParsed()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.DoesNotContain(Visible(), v => v.Contains("thief"));
    }

    [Fact]
    public void ZeroIsALegalValue()
    {
        EnterCombat("ox");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value ox"))));
        Prompt();
        Feed("value ox\r\n");
        Prompt();
        Feed("The value of the ox is 0 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "ox" && r.Value == 0)));
    }

    // A NEGATIVE value is a real wire shape, not a defensive guess: 36 occurrences across 21
    // distinct forms in the session recordings plus clogs (2026-09-04), all of them in this same
    // "the <name>" form. Verbatim from the corpus. Two things had to change together for this to
    // pass - the pattern's [\d,]+ and int.TryParse's NumberStyles.None, either of which alone
    // silently swallows the reply and never raises CreatureValueResolved.
    [Fact]
    public void ANegativeValue_IsParsed()
    {
        EnterCombat("map");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value map"))));
        Prompt();
        Feed("value map\r\n");
        Prompt();
        Feed("The value of the map is -12 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "map" && r.Value == -12)));
        Assert.DoesNotContain(Visible(), v => v.Contains("map"));
    }

    // MUD2 agrees the noun with the number: "1 point.", not "1 points.". 6 occurrences across 5
    // distinct forms in the same corpus. A hardcoded "points." tail drops every one.
    [Fact]
    public void ASingularPoint_IsParsed()
    {
        EnterCombat("penny");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value penny"))));
        Prompt();
        Feed("value penny\r\n");
        Prompt();
        Feed("The value of the penny is 1 point.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "penny" && r.Value == 1)));
        Assert.DoesNotContain(Visible(), v => v.Contains("penny"));
    }

    [Fact]
    public void NumberedInstance_IsAttributedByItsFullEchoedName()
    {
        EnterCombat("rat9");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value rat9"))));
        Prompt();
        Feed("value rat9\r\n");
        Prompt();
        Feed("The value of the rat9 is 22 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "rat9" && r.Value == 22)));
    }

    [Fact]
    public void UnnumberedName_IsAttributedByNameAlone()
    {
        // No instance number, and potentially shared by more than one live creature - the value
        // still attaches to the name, honestly, with no per-instance claim.
        EnterCombat("banshee");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value banshee"))));
        Prompt();
        Feed("value banshee\r\n");
        Prompt();
        Feed("The value of the banshee is 640 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "banshee" && r.Value == 640)));
    }

    [Fact]
    public void BadTarget_ProducesNoValue_AndIsStillSwallowed()
    {
        EnterCombat("vase1");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value vase1"))));
        Prompt();
        Feed("value vase1\r\n");
        Prompt();
        Feed("I don't know to what \"vase1\" you're referring.\r\n");
        Prompt();
        _clock.Advance(TimeSpan.FromMilliseconds(200));   // give a wrongly-unswallowed line its chance to surface
        Assert.Empty(Resolved());
        Assert.DoesNotContain(Visible(), v => v.Contains("vase1"));
    }

    [Fact]
    public void SeveralRepliesFromOneRequest_AreAllCaptured_WithNoPositionalPairing()
    {
        EnterCombat("gargoyle0");
        // A second participant joins inside the same quiet period, before the first probe fires -
        // both are folded into ONE `value` command (the whole point of batching the roster).
        Feed("The gargoyle1 is glaring at you madly.\r\n");
        Assert.True(Settled(_clock, () => Sent().Any(o =>
            o.Contains("value gargoyle0 and gargoyle1") || o.Contains("value gargoyle1 and gargoyle0"))));

        Prompt();
        Feed("value gargoyle0 and gargoyle1\r\n");
        Prompt();
        // Replies arrive in the OPPOSITE order from the request - the direct test that attribution
        // is by NAME, never by request/reply position (the domain's own "value gg" answering both
        // gargoyle0 and gargoyle1 in whichever order the game prints them).
        Feed("The value of the gargoyle1 is 300 points.\r\n");
        Feed("The value of the gargoyle0 is 150 points.\r\n");
        Prompt();

        Assert.True(Settled(_clock, () => Resolved().Count >= 2));
        Assert.Contains(Resolved(), r => r.Name == "gargoyle0" && r.Value == 150);
        Assert.Contains(Resolved(), r => r.Name == "gargoyle1" && r.Value == 300);
    }

    // -- Matrix row: "prompt, echo+reply" - one frame, not two ----------------------------------

    [Fact]
    public void EchoAndReply_InTheSameFrame_StillResolve()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.DoesNotContain(Visible(), v => v.Contains("thief"));
    }

    // -- Matrix row: an unrelated frame arrives first -------------------------------------------
    // Closing on the FIRST prompt seen after arming would let an intervening frame - any frame -
    // shut the window before the probe's own echo/reply ever arrived. The window must not close
    // on ANY prompt; only on the one following full account-for.

    [Fact]
    public void AnUnrelatedFrameArrivingFirst_DoesNotCloseTheWindowEarly()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));

        Prompt();
        Feed("You see nothing unusual.\r\n");   // an unrelated frame, no relation to the probe at all
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();

        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.Contains(Visible(), v => v.Contains("nothing unusual"));
    }

    // -- Matrix row: echo + reply with no prompt at all -----------------------------------------
    // Content-line matching never depends on frame boundaries; only the window's CLOSE does.

    [Fact]
    public void EchoAndReply_WithNoPromptAtAll_StillResolve()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Feed("value thief\r\n");
        Feed("The value of the thief is 1,419 points.\r\n");
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
    }

    [Fact]
    public void TwoInFlightSlots_APlayerSniffAndACreatureProbe_ResolveIndependently()
    {
        // A short heartbeat so the queued player sniff actually rides a real FES,FEW beat while the
        // creature probe (sent standalone, on its own debounce timer) is separately in flight - the
        // direct proof that the two in-flight slots, discriminated by target, do not eat each
        // other's replies. Frame realism is not this test's concern (that is what the matrix-row
        // cases above cover) - it exercises the SNIFF/creature independence, which does not depend
        // on prompt framing at all.
        var clock = new VirtualSessionClock();
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromMilliseconds(150),
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),
        });
        clock.Attach(session);
        var sent = new List<string>();
        var resolvedValues = new List<(string Name, int Value)>();
        var sniffs = new List<(string Name, SniffOutcome Outcome)>();
        var gate = new object();
        session.OutgoingBytes += b => { lock (gate) sent.Add(Encoding.Latin1.GetString(b)); };
        session.CreatureValueResolved += (n, v) => { lock (gate) resolvedValues.Add((n, v)); };
        session.SniffResult += (n, o) => { lock (gate) sniffs.Add((n, o)); };

        session.Feed(GameModeEntry);
        session.QueueValueProbe("Polly");
        session.Feed(Encoding.Latin1.GetBytes("You attack the rat21, using the axe0 as a weapon.\r\n"));

        bool SentContains(string needle) { lock (gate) return sent.Any(o => o.Contains(needle)); }

        // Both requests genuinely outstanding at once - not sent one after the other resolving in
        // between.
        Assert.True(Settled(clock, () => SentContains("value Polly")));
        Assert.True(Settled(clock, () => SentContains("value rat21")));

        // Player-presence wording ("The value of {Name} the {title} is {n} points.") - structurally
        // distinct from a creature reply ("The value of the {name} is {n} points.") per
        // TryConsumeSniffLine's anchored match, so this must resolve as the SNIFF and must NOT be
        // mistaken for (or steal) the creature probe's reply.
        session.Feed(Encoding.Latin1.GetBytes("The value of Polly the witch is 4,120 points.\r\n"));
        Assert.True(Settled(clock, () =>
        {
            lock (gate) return sniffs.Any(s => s.Name == "Polly" && s.Outcome == SniffOutcome.Present);
        }));
        lock (gate) Assert.DoesNotContain(resolvedValues, r => r.Name == "Polly");

        session.Feed(Encoding.Latin1.GetBytes("The value of the rat21 is 87 points.\r\n"));
        Assert.True(Settled(clock, () =>
        {
            lock (gate) return resolvedValues.Any(r => r.Name == "rat21" && r.Value == 87);
        }));
    }

    /// <summary>
    /// A creature reply for "ram2" must not satisfy a queued sniff for persona "Ram" merely
    /// because "The value of the ram2 is 313 points." contains "Ram" as a substring - swallowing
    /// the creature's value entirely and asserting a false player sighting would be a false
    /// positive in the PK-awareness path of a permadeath game. TryConsumeSniffLine anchors on
    /// where the sniffed name sits in the reply, which a creature's "the {name}" wording can never
    /// satisfy for an unrelated persona name.
    /// </summary>
    [Fact]
    public void ACreatureReply_ForANameContainingAQueuedSniffsPersona_DoesNotResolveTheSniff()
    {
        var clock = new VirtualSessionClock();
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromMilliseconds(150),
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),
        });
        clock.Attach(session);
        var resolvedValues = new List<(string Name, int Value)>();
        var sniffs = new List<(string Name, SniffOutcome Outcome)>();
        var gate = new object();
        var sent = new List<string>();
        session.OutgoingBytes += b => { lock (gate) sent.Add(Encoding.Latin1.GetString(b)); };
        session.CreatureValueResolved += (n, v) => { lock (gate) resolvedValues.Add((n, v)); };
        session.SniffResult += (n, o) => { lock (gate) sniffs.Add((n, o)); };

        session.Feed(GameModeEntry);
        session.QueueValueProbe("Ram");
        session.Feed(Encoding.Latin1.GetBytes("You attack the ram2, using the axe0 as a weapon.\r\n"));

        bool SentContains(string needle) { lock (gate) return sent.Any(o => o.Contains(needle)); }
        Assert.True(Settled(clock, () => SentContains("value Ram")));
        Assert.True(Settled(clock, () => SentContains("value ram2")));

        session.Feed(Encoding.Latin1.GetBytes("The value of the ram2 is 313 points.\r\n"));

        Assert.True(Settled(clock, () =>
        {
            lock (gate) return resolvedValues.Any(r => r.Name == "ram2" && r.Value == 313);
        }));
        lock (gate)
        {
            Assert.DoesNotContain(sniffs, s => s.Name == "Ram");
            Assert.Empty(sniffs);
        }
    }

    [Fact]
    public void ALateJoiner_TriggersASecondProbe_OnceTheFirstBatchsWindowCloses()
    {
        EnterCombat("rat17");
        Assert.True(Settled(_clock, () => Sent().Contains("value rat17\r\n")));

        // A second creature joins AFTER the first batch already went out - too late to fold in, so
        // it must not be silently left without a value once known. It stays queued while the first
        // batch's own window is still open.
        Feed("The rat18 is snarling at you hungrily.\r\n");
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));

        // A bare prompt with NOTHING accounted for must NOT close the window - the second batch
        // must still not have gone out.
        Prompt();
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));

        // Only once rat17 is fully accounted for (its reply seen) AND the frame's closing prompt
        // arrives does the window shut and let the second batch go out.
        Feed("value rat17\r\n");
        Prompt();
        Feed("The value of the rat17 is 22 points.\r\n");
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));   // still open - no closing prompt yet

        Prompt();
        Assert.True(Settled(_clock, () => Sent().Contains("value rat18\r\n")));
    }

    /// <summary>
    /// Unnumbered mobs (thief, banshee, coot, fox - see NpcPoolKey's own remarks) have no instance
    /// number and can share a live name, so ONE `value thief` command can legitimately draw a reply
    /// from each of two different creatures. This test proves the SESSION layer captures BOTH
    /// replies (neither leaks to the terminal unswallowed) - see FightAccumulator.NoteValueTests /
    /// ClogWriterTests for the downstream half: turning "resolved twice" into an honest
    /// ambiguous/unknown reading rather than a last-writer-wins coin-flip.
    /// </summary>
    [Fact]
    public void UnnumberedNameSharedByTwoLiveCreatures_BothRepliesAreCaptured_NeitherLeaked()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Feed("The value of the thief is 87 points.\r\n");
        Prompt();

        Assert.True(Settled(_clock, () => Resolved().Count(r => r.Name == "thief") == 2));
        Assert.Contains(Resolved(), r => r.Name == "thief" && r.Value == 1419);
        Assert.Contains(Resolved(), r => r.Name == "thief" && r.Value == 87);
        Assert.DoesNotContain(Visible(), v => v.Contains("thief"));
    }

    /// <summary>
    /// Backstop: a requested name that never draws any reply at all (ambiguous match consumed
    /// elsewhere, or the creature left before answering) must not wedge the window open forever -
    /// see MudSessionOptions.CreatureValueProbeTimeout.
    /// </summary>
    [Fact]
    public void ANameThatNeverReplies_IsGivenUpOnByTheTimeoutBackstop_AndDoesNotWedgeTheWindow()
    {
        var clock = new VirtualSessionClock();
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval      = TimeSpan.FromSeconds(60),
            StaleProbeDelay           = TimeSpan.FromSeconds(30),
            MinProbeSpacing           = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce    = TimeSpan.FromMilliseconds(80),
            CreatureValueProbeTimeout = TimeSpan.FromMilliseconds(200),
        });
        clock.Attach(session);
        var sent = new List<string>();
        var gate = new object();
        session.OutgoingBytes += b => { lock (gate) sent.Add(Encoding.Latin1.GetString(b)); };
        List<string> Sent() { lock (gate) return sent.ToList(); }

        session.Feed(GameModeEntry);
        session.Feed(Encoding.Latin1.GetBytes("You attack the zombie4, using the axe0 as a weapon.\r\n"));
        Assert.True(Settled(clock, () => Sent().Any(o => o.Contains("value zombie4"))));

        // Nothing ever answers it - no echo, no reply. A second creature joins and must eventually
        // get its own probe once the backstop gives up on the first.
        session.Feed(Encoding.Latin1.GetBytes("The zombie5 is snarling at you hungrily.\r\n"));

        Assert.True(Settled(clock, () => Sent().Any(o => o.Contains("value zombie5")), TimeSpan.FromSeconds(3)));
    }

    /// <summary>
    /// Value is NOT a fixed per-species constant: it is cumulative and climbs with what the
    /// individual creature has scored (swamping items, big jumps off a player's death or a failed
    /// flee - see MudSession's own remarks on <c>_creatureValueKnown</c>). A session-lifetime
    /// "already answered" cache would suppress the second encounter's probe entirely and leave the
    /// stale first reading in place. This test drives real wire bytes for TWO separate encounters
    /// against the same literal name with two DIFFERENT readings, and confirms the probe fires
    /// again for the second fight, then carries that second reading all the way to a RosterRow via
    /// the same CombatStatsAggregator/ParticipantRoster path the app uses.
    /// </summary>
    [Fact]
    public void SameCreatureName_TwoSeparateEncounters_TheSecondEncountersValueReachesTheRosterRow()
    {
        EnterCombat("thief");
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Feed("The value of the thief is 100 points.\r\n");
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 100)));

        // Close the first encounter outright (a kill - the sole active participant).
        Feed("You have killed the thief.\r\n");
        Assert.False(_session.InCombat);
        lock (_lock) { _outgoing.Clear(); _resolved.Clear(); }

        // A second, separate encounter against a creature sharing the SAME literal name. Without
        // per-encounter clearing, `_creatureValueKnown` would still contain "thief" from the first
        // fight and this probe would never be sent at all.
        Feed("You attack the thief, using the axe0 as a weapon.\r\n");
        Assert.True(_session.InCombat);
        Assert.True(Settled(_clock, () => Sent().Any(o => o.Contains("value thief"))));
        Feed("The value of the thief is 1,419 points.\r\n");
        Assert.True(Settled(_clock, () => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));

        var secondValue = Resolved().Single(r => r.Name == "thief").Value;

        // Carry that second reading through the same path the live app does: a per-encounter
        // CombatStatsAggregator bucket, then ParticipantRoster.Build's row.
        var aggregator = new CombatStatsAggregator();
        var start = DateTime.UtcNow;
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(
            start, CombatEventKind.FightStart, CombatActor.Player, "thief", "axe0", null, null, ""));
        aggregator.ObserveCreatureValue("thief", secondValue);

        var fight = aggregator.Snapshot(start).Fights.Single(f => f.NpcName == "thief");
        var fact = new ParticipantFact(fight.NpcName, fight.IsResolved, fight.Outcome, Value: fight.Value);
        var plan = ParticipantRoster.Build([fact]);

        Assert.Equal(1419, plan.Rows.Single().Value);
    }

    // -- The probe must never type at the shell ------------------------------------------------
    //
    // The player dies. MUD2's death signal is C08 C13 ("Not updating persona.", Bartle 08 13)
    // alongside "The <npc> has killed you." and a drop to the login shell - but the parser only
    // LEAVES game mode when it matches the "Option:" prompt later in the stream, so `InGameMode`
    // is still true across that window while the far end is already a shell. With a name still
    // queued and the debounce timer still armed, the probe would otherwise send `value <name>`
    // into it. Permadeath game; an injected command at the shell is not cosmetic. Both tests below
    // use their own session with a debounce long enough to make the queue-then-die ordering
    // deterministic rather than a race.

    private static MudSession NewSlowDebounceSession(List<string> outgoing, object gate, VirtualSessionClock clock)
    {
        var s = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromSeconds(60),
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(400),
        });
        clock.Attach(s);
        s.OutgoingBytes += b => { lock (gate) outgoing.Add(Encoding.Latin1.GetString(b)); };
        return s;
    }

    [Fact]
    public void PlayerKilled_WithANameStillQueued_SendsNoValueCommand_EvenThoughGameModeHasNotExitedYet()
    {
        var outgoing = new List<string>();
        var gate = new object();
        var clock = new VirtualSessionClock();
        using var s = NewSlowDebounceSession(outgoing, gate, clock);

        s.Feed(GameModeEntry);
        s.Feed(Encoding.Latin1.GetBytes("You attack the thief, using the axe0 as a weapon.\r\n"));
        Assert.True(s.InCombat);
        lock (gate) outgoing.Clear();

        // Killed, well inside the 400 ms debounce - the probe for "thief" is queued, not yet sent.
        s.Feed(Encoding.Latin1.GetBytes("The thief has killed you.\r\n"));
        Assert.False(s.InCombat);
        // The exact window that made this reachable: combat is over, the shell is next, and the
        // parser still believes it is in game mode.
        Assert.True(s.InGameMode);

        // Well past the debounce. Nothing may go out.
        Assert.False(Settled(clock,
            () => { lock (gate) return outgoing.Any(o => o.Contains("value", StringComparison.Ordinal)); },
            TimeSpan.FromMilliseconds(1200)));
    }

    [Fact]
    public void AfterAnEncounterEnds_ALateParticipantJoinCannotReArmTheProbe()
    {
        var outgoing = new List<string>();
        var gate = new object();
        var clock = new VirtualSessionClock();
        using var s = NewSlowDebounceSession(outgoing, gate, clock);

        s.Feed(GameModeEntry);
        s.Feed(Encoding.Latin1.GetBytes("You attack the thief, using the axe0 as a weapon.\r\n"));
        s.Feed(Encoding.Latin1.GetBytes("You have killed the thief.\r\n"));
        Assert.False(s.InCombat);
        lock (gate) outgoing.Clear();

        // A stray line that names a creature but opens no encounter must not put anything on the
        // wire: with the queue cleared at encounter end AND the InCombat gate in
        // TakeCreatureValueProbeLocked, there is nothing left to re-arm.
        s.Feed(Encoding.Latin1.GetBytes("The thief walks away, wearily.\r\n"));
        Assert.False(s.InCombat);
        Assert.False(Settled(clock,
            () => { lock (gate) return outgoing.Any(o => o.Contains("value", StringComparison.Ordinal)); },
            TimeSpan.FromSeconds(1)));
    }
}
