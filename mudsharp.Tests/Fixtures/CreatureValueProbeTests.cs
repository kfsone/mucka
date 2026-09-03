using System.Text;
using Mucka.ViewModels;
using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The in-combat creature-value probe: `value &lt;name&gt;` reports the points awarded for killing
/// a creature (operator, 2026-09-02). Learned once per newly-active roster name, batched across
/// whatever joined since the last quiet period ("value x and y and z" costs ONE server tick), and
/// swallowed from the terminal the same way the player-presence sniff already is - see
/// MudSession's "In-combat creature value probe" remarks for the design this exercises.
///
/// <para><b>Wire shape, and why every case below leads a frame with its prompt.</b> On real wire
/// traffic every server frame is LED by an IsPartial '*' prompt (verified: PostSelectSetupTests'
/// own model, taken from a live capture) - so a `value` command's echo and its reply each arrive
/// in a frame that STARTS with a prompt, not one that ends with one. An earlier version of this
/// probe (and this file) assumed the opposite - that the in-flight window closes on the first
/// prompt seen after arming - which meant it closed before the echo, let alone the reply, on every
/// realistic shape (review, 2026-09-02). The fix tracks which requested names are still
/// unaccounted-for and only actually closes the window at the frame boundary that FOLLOWS the
/// point where all of them have drawn a reply or bad-target rejection - see
/// TryConsumeCreatureValueLine's own remarks. The cases below exercise the shapes the reviewer's
/// matrix specifically named.</para>
/// </summary>
public class CreatureValueProbeTests : IDisposable
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // One frame-leading '*' prompt (IsPartial), verbatim from PostSelectSetupTests - a `value`
    // command's own frame starts here, exactly like any other typed command's.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly MudSession _session;
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
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
        _session.CreatureValueResolved += (name, value) => { lock (_lock) _resolved.Add((name, value)); };
        _session.LineReady += l => { if (!l.IsPartial) lock (_lock) _visible.Add(l.PlainText); };
    }

    public void Dispose() => _session.Dispose();

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

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
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
        Assert.True(WaitFor(() => Sent().Contains("value rat17\r\n")));
    }

    // ── Matrix row: "prompt, echo" / "prompt, reply" - the documented shape ────────────────────
    // (MudSession.cs:166's own claim, and PostSelectSetupTests' model): echo and reply each arrive
    // as their OWN frame, each led by its own prompt.

    [Fact]
    public void ThousandsSeparator_IsParsed()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.DoesNotContain(Visible(), v => v.Contains("thief"));
    }

    [Fact]
    public void ZeroIsALegalValue()
    {
        EnterCombat("ox");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value ox"))));
        Prompt();
        Feed("value ox\r\n");
        Prompt();
        Feed("The value of the ox is 0 points.\r\n");
        Prompt();
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "ox" && r.Value == 0)));
    }

    [Fact]
    public void NumberedInstance_IsAttributedByItsFullEchoedName()
    {
        EnterCombat("rat9");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value rat9"))));
        Prompt();
        Feed("value rat9\r\n");
        Prompt();
        Feed("The value of the rat9 is 22 points.\r\n");
        Prompt();
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "rat9" && r.Value == 22)));
    }

    [Fact]
    public void UnnumberedName_IsAttributedByNameAlone()
    {
        // No instance number, and (per the operator) potentially shared by more than one live
        // creature - the value still attaches to the name, honestly, with no per-instance claim.
        EnterCombat("banshee");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value banshee"))));
        Prompt();
        Feed("value banshee\r\n");
        Prompt();
        Feed("The value of the banshee is 640 points.\r\n");
        Prompt();
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "banshee" && r.Value == 640)));
    }

    [Fact]
    public void BadTarget_ProducesNoValue_AndIsStillSwallowed()
    {
        EnterCombat("vase1");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value vase1"))));
        Prompt();
        Feed("value vase1\r\n");
        Prompt();
        Feed("I don't know to what \"vase1\" you're referring.\r\n");
        Prompt();
        Thread.Sleep(200);   // give a wrongly-unswallowed line time to surface
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
        Assert.True(WaitFor(() => Sent().Any(o =>
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

        Assert.True(WaitFor(() => Resolved().Count >= 2));
        Assert.Contains(Resolved(), r => r.Name == "gargoyle0" && r.Value == 150);
        Assert.Contains(Resolved(), r => r.Name == "gargoyle1" && r.Value == 300);
    }

    // ── Matrix row: "prompt, echo+reply" - one frame, not two ──────────────────────────────────

    [Fact]
    public void EchoAndReply_InTheSameFrame_StillResolve()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.DoesNotContain(Visible(), v => v.Contains("thief"));
    }

    // ── Matrix row: an unrelated frame arrives first ───────────────────────────────────────────
    // Under the bug, closing on the FIRST prompt seen after arming meant an intervening frame -
    // any frame - shut the window before the probe's own echo/reply ever arrived. The fix must not
    // close on ANY prompt; only on the one following full account-for.

    [Fact]
    public void AnUnrelatedFrameArrivingFirst_DoesNotCloseTheWindowEarly()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));

        Prompt();
        Feed("You see nothing unusual.\r\n");   // an unrelated frame, no relation to the probe at all
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Prompt();

        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
        Assert.Contains(Visible(), v => v.Contains("nothing unusual"));
    }

    // ── Matrix row: echo + reply with no prompt at all ─────────────────────────────────────────
    // The one shape the OLD (buggy) code happened to work on. Kept to prove the fix does not
    // regress it - content-line matching never depended on frame boundaries; only the window's
    // CLOSE did.

    [Fact]
    public void EchoAndReply_WithNoPromptAtAll_StillResolve()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Feed("value thief\r\n");
        Feed("The value of the thief is 1,419 points.\r\n");
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));
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
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromMilliseconds(150),
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),
        });
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
        Assert.True(WaitFor(() => SentContains("value Polly")));
        Assert.True(WaitFor(() => SentContains("value rat21")));

        // Player-presence wording ("The value of {Name} the {title} is {n} points.") - structurally
        // distinct from a creature reply ("The value of the {name} is {n} points.") per
        // TryConsumeSniffLine's anchored match, so this must resolve as the SNIFF and must NOT be
        // mistaken for (or steal) the creature probe's reply.
        session.Feed(Encoding.Latin1.GetBytes("The value of Polly the witch is 4,120 points.\r\n"));
        Assert.True(WaitFor(() =>
        {
            lock (gate) return sniffs.Any(s => s.Name == "Polly" && s.Outcome == SniffOutcome.Present);
        }));
        lock (gate) Assert.DoesNotContain(resolvedValues, r => r.Name == "Polly");

        session.Feed(Encoding.Latin1.GetBytes("The value of the rat21 is 87 points.\r\n"));
        Assert.True(WaitFor(() =>
        {
            lock (gate) return resolvedValues.Any(r => r.Name == "rat21" && r.Value == 87);
        }));
    }

    /// <summary>
    /// Regression for the sniff/creature collision (review, 2026-09-02): a creature reply for
    /// "ram2" satisfied a queued sniff for persona "Ram" under the OLD bare
    /// <c>Contains(name, OrdinalIgnoreCase)</c> match, because "The value of the ram2 is 313
    /// points." contains "Ram" as a substring. That swallowed the creature's value entirely AND
    /// asserted a false player sighting - a false positive in the PK-awareness path of a permadeath
    /// game. TryConsumeSniffLine now anchors on where the sniffed name sits in the reply, which a
    /// creature's "the {name}" wording can never satisfy for an unrelated persona name.
    /// </summary>
    [Fact]
    public void ACreatureReply_ForANameContainingAQueuedSniffsPersona_DoesNotResolveTheSniff()
    {
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromMilliseconds(150),
            StaleProbeDelay        = TimeSpan.FromSeconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),
        });
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
        Assert.True(WaitFor(() => SentContains("value Ram")));
        Assert.True(WaitFor(() => SentContains("value ram2")));

        session.Feed(Encoding.Latin1.GetBytes("The value of the ram2 is 313 points.\r\n"));

        Assert.True(WaitFor(() =>
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
        Assert.True(WaitFor(() => Sent().Contains("value rat17\r\n")));

        // A second creature joins AFTER the first batch already went out - too late to fold in, so
        // it must not be silently left without a value once known. It stays queued while the first
        // batch's own window is still open.
        Feed("The rat18 is snarling at you hungrily.\r\n");
        Thread.Sleep(150);
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));

        // A bare prompt with NOTHING accounted for must NOT close the window (the bug this file was
        // rewritten to stop asserting) - the second batch must still not have gone out.
        Prompt();
        Thread.Sleep(150);
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));

        // Only once rat17 is fully accounted for (its reply seen) AND the frame's closing prompt
        // arrives does the window shut and let the second batch go out.
        Feed("value rat17\r\n");
        Prompt();
        Feed("The value of the rat17 is 22 points.\r\n");
        Thread.Sleep(150);
        Assert.DoesNotContain(Sent(), o => o.Contains("rat18"));   // still open - no closing prompt yet

        Prompt();
        Assert.True(WaitFor(() => Sent().Contains("value rat18\r\n")));
    }

    /// <summary>
    /// Unnumbered mobs (thief, banshee, coot, fox - see NpcPoolKey's own remarks) have no instance
    /// number and can share a live name, so ONE `value thief` command can legitimately draw a reply
    /// from each of two different creatures. Reviewer's executed matrix: both replies resolved
    /// silently with the roster ending up holding whichever arrived last, with nothing marking them
    /// unattributable. This test proves the SESSION layer captures BOTH replies (neither leaks to
    /// the terminal unswallowed) - see FightAccumulator.NoteValueTests / ClogWriterTests for the
    /// downstream half: turning "resolved twice" into an honest ambiguous/unknown reading rather
    /// than a last-writer-wins coin-flip.
    /// </summary>
    [Fact]
    public void UnnumberedNameSharedByTwoLiveCreatures_BothRepliesAreCaptured_NeitherLeaked()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Prompt();
        Feed("value thief\r\n");
        Prompt();
        Feed("The value of the thief is 1,419 points.\r\n");
        Feed("The value of the thief is 87 points.\r\n");
        Prompt();

        Assert.True(WaitFor(() => Resolved().Count(r => r.Name == "thief") == 2));
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
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval      = TimeSpan.FromSeconds(60),
            StaleProbeDelay           = TimeSpan.FromSeconds(30),
            MinProbeSpacing           = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce    = TimeSpan.FromMilliseconds(80),
            CreatureValueProbeTimeout = TimeSpan.FromMilliseconds(200),
        });
        var sent = new List<string>();
        var gate = new object();
        session.OutgoingBytes += b => { lock (gate) sent.Add(Encoding.Latin1.GetString(b)); };
        List<string> Sent() { lock (gate) return sent.ToList(); }

        session.Feed(GameModeEntry);
        session.Feed(Encoding.Latin1.GetBytes("You attack the zombie4, using the axe0 as a weapon.\r\n"));
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value zombie4"))));

        // Nothing ever answers it - no echo, no reply. A second creature joins and must eventually
        // get its own probe once the backstop gives up on the first.
        session.Feed(Encoding.Latin1.GetBytes("The zombie5 is snarling at you hungrily.\r\n"));

        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value zombie5")), timeoutMs: 3000));
    }

    /// <summary>
    /// Regression guard: value is NOT a fixed per-species constant, it is cumulative and climbs
    /// with what the individual creature has scored (swamping items, big jumps off a player's
    /// death or a failed flee - see MudSession's own remarks on <c>_creatureValueKnown</c>). A
    /// session-lifetime "already answered" cache would suppress the second encounter's probe
    /// entirely and leave the stale first reading in place - exactly the bug the owner reported
    /// after being killed by a thief, logging back in, and finding its value had moved. This test
    /// fails against that behaviour: it drives real wire bytes for TWO separate encounters against
    /// the same literal name with two DIFFERENT readings, and confirms the probe fires again for
    /// the second fight, then carries that second reading all the way to a RosterRow via the same
    /// CombatStatsAggregator/ParticipantRoster path the app uses.
    /// </summary>
    [Fact]
    public void SameCreatureName_TwoSeparateEncounters_TheSecondEncountersValueReachesTheRosterRow()
    {
        EnterCombat("thief");
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Feed("The value of the thief is 100 points.\r\n");
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 100)));

        // Close the first encounter outright (a kill - the sole active participant).
        Feed("You have killed the thief.\r\n");
        Assert.False(_session.InCombat);
        lock (_lock) { _outgoing.Clear(); _resolved.Clear(); }

        // A second, separate encounter against a creature sharing the SAME literal name. Under the
        // bug, `_creatureValueKnown` would still contain "thief" from the first fight and this
        // probe would never be sent at all.
        Feed("You attack the thief, using the axe0 as a weapon.\r\n");
        Assert.True(_session.InCombat);
        Assert.True(WaitFor(() => Sent().Any(o => o.Contains("value thief"))));
        Feed("The value of the thief is 1,419 points.\r\n");
        Assert.True(WaitFor(() => Resolved().Any(r => r.Name == "thief" && r.Value == 1419)));

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
}
