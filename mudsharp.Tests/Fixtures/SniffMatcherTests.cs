using System.Text;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <c>MudSession.TryConsumeSniffLine</c> - the matcher that turns a <c>value &lt;persona&gt;</c>
/// reply into an online/offline verdict about another player. In a permadeath game with PKers in
/// it, both a fabricated sighting and a fabricated "they logged out" are safety-relevant, so this
/// file pins BOTH directions.
///
/// <para><b>Grammar, from captured wire traffic</b> (raw session recordings under
/// <c>%LOCALAPPDATA%\Temp\mucka</c>, plus <c>~/.mucka/clogs</c>; 458 "The value of ..." lines, 246
/// distinct; 46 "I don't know the word" lines, 40 distinct words). The two player replies below are
/// verbatim corpus captures, each identified by its own <c>val &lt;name&gt;</c> echo on the
/// preceding tx frame. Nothing here is an invented shape except where a comment says so.</para>
/// </summary>
public class SniffMatcherTests
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    private sealed class Harness : IDisposable
    {
        /// <summary>The heartbeat a queued sniff rides out on.</summary>
        private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(150);

        public readonly MudSession Session;
        private readonly VirtualSessionClock _clock = new();
        private readonly object _gate = new();
        private readonly List<(string Name, SniffOutcome Outcome)> _sniffs = new();
        private readonly List<string> _visible = new();
        private readonly List<string> _sent = new();

        public Harness()
        {
            Session = new MudSession(new MudSessionOptions
            {
                FesHeartbeatInterval   = Beat,
                StaleProbeDelay        = TimeSpan.FromSeconds(30),
                MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
                InventoryProbeDebounce = TimeSpan.FromMilliseconds(80),
            });
            _clock.Attach(Session);
            Session.SniffResult += (n, o) => { lock (_gate) _sniffs.Add((n, o)); };
            Session.OutgoingBytes += b => { lock (_gate) _sent.Add(Encoding.Latin1.GetString(b)); };
            Session.LineReady += l => { if (!l.IsPartial) lock (_gate) _visible.Add(l.PlainText); };
        }

        public void Dispose() => Session.Dispose();

        /// <summary>Enter game mode, queue a sniff, and step the clock to the beat it rides out on -
        /// only once it is on the wire is the in-flight slot armed and the matcher reachable.</summary>
        public void ArmSniff(string persona)
        {
            Session.Feed(GameModeEntry);
            Session.QueueValueProbe(persona);
            _clock.Advance(Beat);
            Assert.Contains(Sent(), s => s.Contains("value " + persona, StringComparison.Ordinal));
            lock (_gate) _visible.Clear();
        }

        public void Feed(string line) => Session.Feed(Encoding.Latin1.GetBytes(line + "\r\n"));

        public List<(string Name, SniffOutcome Outcome)> Sniffs() { lock (_gate) return _sniffs.ToList(); }
        public List<string> Visible() { lock (_gate) return _visible.ToList(); }
        private List<string> Sent() { lock (_gate) return _sent.ToList(); }

        /// <summary>The matcher runs on the Feed thread, so a verdict is already in by the time the
        /// line that decided it has returned - nothing here waits.</summary>
        public bool Resolved(string name, SniffOutcome outcome)
            => Sniffs().Any(s => s.Name == name && s.Outcome == outcome);

        /// <summary>Nothing resolved, with the clock stepped past the next beat so a probe that
        /// would have carried a verdict has had its chance.</summary>
        public bool NothingResolved()
        {
            _clock.Advance(Beat);
            return Sniffs().Count == 0;
        }
    }

    // -- Outcome 2: the vocabulary rejection ---------------------------------------------------
    //
    // Shape: I don't know the word "<word>".   Always ASCII quotes, always a trailing full stop;
    // no other variant observed in 46 captures. The words inside are overwhelmingly the PLAYER'S
    // OWN TYPOS: "dro", "clsoe", "kep", "lioin", "krat11", "atomcibob", ... A line whose whole text
    // merely CONTAINS the sniffed persona must not resolve an unrelated sniff to Offline.

    [Fact]
    public void AVocabularyRejectionForSomeoneElsesTypo_DoesNotResolveTheSniff_AndIsNotSwallowed()
    {
        using var h = new Harness();
        h.ArmSniff("Rat");
        // Corpus-real line (raw recordings, 1 occurrence). Contains "rat"; is not about a persona.
        h.Feed("I don't know the word \"krat11\".");
        Assert.True(h.NothingResolved());
        // And it must still reach the terminal - it is the player's own typo, not our traffic.
        Assert.Contains(h.Visible(), v => v.Contains("krat11", StringComparison.Ordinal));
    }

    [Fact]
    public void AVocabularyRejectionContainingThePersonaInAnotherWord_DoesNotResolveTheSniff()
    {
        using var h = new Harness();
        h.ArmSniff("Bob");
        // Corpus-real (2 occurrences): a mistyped persona in a directed-speech command. Contains
        // "bob"; says nothing whatever about a player called Bob.
        h.Feed("I don't know the word \"atomcibob\".");
        Assert.True(h.NothingResolved());
    }

    [Fact]
    public void TheVocabularyRejectionForTheSniffedNameItself_ResolvesOffline_AndIsSwallowed()
    {
        using var h = new Harness();
        h.ArmSniff("Polly");
        // Lower-cased: the server canonicalises case (tx "val crispybob" -> rx "...of Crispybob..."),
        // so the comparison must be case-insensitive.
        h.Feed("I don't know the word \"polly\".");
        Assert.True(h.Resolved("Polly", SniffOutcome.Offline));
        Assert.DoesNotContain(h.Visible(), v => v.Contains("don't know the word", StringComparison.Ordinal));
    }

    // -- Outcome 1: the presence reply ---------------------------------------------------------

    [Fact]
    public void TheObservedPlayerReply_ResolvesPresent()
    {
        using var h = new Harness();
        h.ArmSniff("Crispybob");
        // Verbatim corpus capture (session-rec 20260902-160323), including the comma grouping.
        h.Feed("The value of Crispybob the necromancer is 5,965 points.");
        Assert.True(h.Resolved("Crispybob", SniffOutcome.Present));
    }

    [Fact]
    public void ATitleThatPRECEDESThePersona_StillResolvesPresent()
    {
        using var h = new Harness();
        h.ArmSniff("Polly");
        // MUD2's own `levels` table (captured verbatim on the wire) makes "Lady" the level-10
        // normal title, and PlayerNameParts - this codebase's one name grammar - models
        // "Sir "/"Lady " as prefixes, so a title-prefixed reply must still resolve Present.
        // NOTE: no "Lady <Name>" player reply exists in the corpus; the shape follows the game's
        // own rank table, not an observation, and is handled precisely because it cannot be ruled
        // out. The suffix form below is the observed one.
        h.Feed("The value of Lady Polly the mage is 102,400 points.");
        Assert.True(h.Resolved("Polly", SniffOutcome.Present));
    }

    [Fact]
    public void ASingularPointReply_StillResolvesPresent()
    {
        using var h = new Harness();
        h.ArmSniff("Penny");
        // "1 point." with no s is real - 6 corpus occurrences ("The value of the penny is 1
        // point."). The old tail required " points." and would have missed a one-point persona.
        h.Feed("The value of Penny the novice is 1 point.");
        Assert.True(h.Resolved("Penny", SniffOutcome.Present));
    }

    [Fact]
    public void ACreatureReplyForTheSniffedNameItself_DoesNotResolvePresent()
    {
        using var h = new Harness();
        h.ArmSniff("Wyvern");
        // The literal "the " after "of " is the discriminator, and it holds even when the creature
        // name IS the sniffed persona name exactly. Corpus-real line.
        h.Feed("The value of the wyvern is 239 points.");
        Assert.True(h.NothingResolved());
    }

    [Fact]
    public void ACreatureReplyForANameContainingTheSniffedPersona_DoesNotResolvePresent()
    {
        using var h = new Harness();
        h.ArmSniff("Ram");
        h.Feed("The value of the ram2 is 313 points.");
        Assert.True(h.NothingResolved());
    }
}
