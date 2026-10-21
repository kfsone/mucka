using Mucka.Combat;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>
/// Random fights against a ground-truth world, checked against what the truth alone decides.
///
/// <para>A world holds up to five creatures (two person-class, three not), each with a true name and
/// class, and a player who can or cannot see. A script of acts is rendered into the lines the
/// player would be shown - a creature the player cannot see is "someone"/"something" in every
/// sentence - and fed to a <see cref="CombatTracker"/>. The oracle asserts only invariants the
/// truth settles on its own:</para>
/// <list type="bullet">
/// <item><b>Soundness</b>: a swing on a named line is credited to that creature, and a swing on an
/// anonymous line is the unseen group's, never a name's.</item>
/// <item><b>Conservation</b>: every swing line produced exactly one swing event, and every start
/// line one fight start.</item>
/// <item><b>Closing</b>: at every prompt the tracker is in combat exactly when the truth has a
/// creature still fighting the player, and counting never concludes a creature the truth says is
/// still fighting.</item>
/// <item><b>Neutrality</b>: a line that is not about the player's fight moves neither the encounter
/// nor the unseen group.</item>
/// </list>
///
/// <para>Not asserted: which outcome counting gives a row, whether a slot conclusion carries the
/// right name, the badges, and any score. <see cref="UnseenBookTests"/> checks counting's outcomes
/// against its own truth; nothing here does.</para>
///
/// <para>Acts that are impossible in the current world (a swing from a creature that never opened a
/// fight, a kill of a dead creature) are skipped at render time, which is also what lets a failing
/// script be shrunk by deleting acts.</para>
/// </summary>
public sealed class UnseenFuzzTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 22, 50, 56, DateTimeKind.Utc);

    private enum ActKind
    {
        CreatureStarts, PlayerStarts, CreatureSwings, PlayerSwings, Kill, Fade, Regain,
        Blind, Sighted, CreatureFlees, CreatureFleeFails, PlayerFlees, PlayerFleeFails, Frame, NonCombat, BystanderEnds, PlayerDies, Withdraws,
    }

    private readonly record struct Act(ActKind Kind, int Who, bool Hit);

    private sealed record Creature(string Name, SomeKind Class)
    {
        public bool InRoom = true;
        public bool Engaged;
        public bool Invisible;
    }

    private static readonly (string Name, SomeKind Class)[] Cast =
    [
        ("man", SomeKind.Someone), ("thief", SomeKind.Someone),
        ("rat1", SomeKind.Something), ("rat2", SomeKind.Something), ("snake1", SomeKind.Something),
    ];

    private sealed class Render
    {
        public readonly List<string> Transcript = [];
        public readonly List<(string Name, bool Anonymous)> Truths = [];
        public readonly List<Creature> World = Cast.Select(c => new Creature(c.Name, c.Class)).ToList();
        public bool CannotSee;
        public readonly List<(string Step, StyledLine? Line)> Steps = [];
        /// <summary>Per step: the creatures still fighting the player once the act that produced it
        /// is done.</summary>
        public readonly List<HashSet<string>> Truth = [];

        public bool Unseen(Creature c) => CannotSee || c.Invisible;

        public bool Dead;

        private static string Word(Creature c) => c.Class == SomeKind.Someone ? "someone" : "something";
        private static string Cap(Creature c) => c.Class == SomeKind.Someone ? "Someone" : "Something";
        public string Subject(Creature c) => Unseen(c) ? Cap(c) : "The " + c.Name;
        public string Object(Creature c) => Unseen(c) ? Word(c) : "the " + c.Name;

        public void Emit(string text, LineKind kind = LineKind.Normal, Creature? swingTruth = null, bool neutral = false)
        {
            var line = new StyledLine([new StyledSpan(text, TextStyle.Default)], kind: kind);
            Steps.Add((neutral ? "neutral" : "line", line));
            Transcript.Add(text);
            if (swingTruth is { } c)
                Truths.Add((c.Name, Unseen(c)));
        }

        /// <summary>Stamina lost or a blow landed outside a fight exchange, verbatim from the wire:
        /// plain uncoded text, then the stamina parenthetical on its own line. The creature-named ones
        /// only while the player can see that creature. None of these is a swing or a start.</summary>
        private List<string> NonCombatLines(Creature c, int pick)
        {
            var seen = c.InRoom && !Unseen(c);
            var lines = new List<string>[]
            {
                ["YEOWW! You burn yourself when you touch the fire.", "(70/100)."],
                ["Rain has swollen the river to a raging torrent! You fight your way across, but are constantly buffeted and pounded all the way, causing you minor injury!", "(70/100)."],
                ["A bright flash of lightning forks down and strikes you.", "(70/100)."],
                ["Out of nowhere, Eros suddenly produces a bow and looses an arrow straight at your heart! It's a direct hit, but almost immediately the missile disappears! You feel a strange longing...", "(70/100)."],
                ["Eros pulls his bow and shoots Fred!"],
                ["You are suddenly struck from above by a pillar of fire!", "Firedemon the hell-bound wizard has burned you!", "(70/100)."],
                seen ? [$"The {c.Name} resists your efforts, and you hurt yourself trying.", "(70/100)."] : ["YOW! That hurt! No need to be so violent!", "(70/100)."],
                seen ? [$"The {c.Name} has just thrown a pebble at you (70/100)."] : ["The parrot gives you a nasty peck!", "(70/100)."],
            };
            return lines[pick % lines.Length];
        }

        public int Starts;

        public void Apply(Act a)
        {
            if (Dead)
                return;
            var c = a.Kind is ActKind.Blind or ActKind.Sighted or ActKind.PlayerFlees
                or ActKind.PlayerFleeFails or ActKind.Frame ? null : World[a.Who % World.Count];
            switch (a.Kind)
            {
                case ActKind.CreatureStarts when c!.InRoom && !c.Engaged:
                    c.Engaged = true;
                    Starts++;
                    Emit(Unseen(c) ? $"{Subject(c)} is about to attack you."
                                   : $"The {c.Name} is moving towards you ferociously.", LineKind.FightStart);
                    break;
                case ActKind.PlayerStarts when c!.InRoom && !c.Engaged:
                    c.Engaged = true;
                    Starts++;
                    Emit($"You attack {Object(c)}.", LineKind.FightStart);
                    break;
                case ActKind.CreatureSwings when c!.InRoom && c.Engaged:
                    Emit(a.Hit ? $"{Subject(c)} hits you (80/100)." : $"{Subject(c)} misses you.",
                        swingTruth: c);
                    break;
                case ActKind.PlayerSwings when c!.InRoom && c.Engaged:
                    Emit(a.Hit ? $"You hit {Object(c)} (10-14)." : $"You miss {Object(c)}.",
                        swingTruth: c);
                    break;
                case ActKind.Kill when c!.InRoom && c.Engaged:
                    Emit($"You have killed {Object(c)}.");
                    c.InRoom = false;
                    c.Engaged = false;
                    break;
                case ActKind.Fade when c!.InRoom && !c.Invisible:
                    c.Invisible = true;
                    // Announced only to a player who sees it happen.
                    if (!CannotSee)
                        Emit($"The {c.Name} fades from view.", LineKind.CreatureInvisible);
                    break;
                case ActKind.Regain when c!.InRoom && c.Invisible:
                    c.Invisible = false;
                    if (!CannotSee)
                        Emit($"The {c.Name} has regained his visibleness!");
                    break;
                case ActKind.CreatureFlees when c!.InRoom && c.Engaged && !Unseen(c):
                    // The player's own fight: coded C08.11, as the wire codes most of these
                    // (docs/evidence/unseen-corpus.md, creature flights).
                    Emit($"The {c.Name} has fled by going east.", LineKind.FightEnd);
                    c.InRoom = false;
                    c.Engaged = false;
                    break;
                case ActKind.CreatureFleeFails when c!.InRoom && c.Engaged && !Unseen(c):
                    Emit($"The {c.Name} has fled by trying to go east.", LineKind.FightEnd);
                    c.Engaged = false;
                    break;
                case ActKind.Withdraws when c!.InRoom && c.Engaged:
                    // Says "your fight": the player's own, named or not.
                    Emit($"{Subject(c)} withdraws from your fight, and so do you.");
                    c.Engaged = false;
                    break;
                case ActKind.PlayerFlees when World.Any(w => w.Engaged):
                    Emit("You have fled by going north.");
                    foreach (var w in World)
                        if (w.Engaged)
                        {
                            w.Engaged = false;
                        }
                    break;
                case ActKind.PlayerFleeFails when World.Any(w => w.Engaged):
                    Emit("You have fled by trying to go north.");
                    foreach (var w in World)
                        w.Engaged = false;
                    break;
                case ActKind.Blind:
                    CannotSee = true;
                    Transcript.Add("<player cannot see>");
                    Steps.Add(("dark", null));
                    return;
                case ActKind.Sighted:
                    CannotSee = false;
                    Transcript.Add("<player can see>");
                    Steps.Add(("light", null));
                    return;
                case ActKind.BystanderEnds when c!.InRoom && !c.Engaged && !Unseen(c):
                    // Endings of a Creature that is not fighting the player: said about the room, not
                    // the fight, and they must not move the tracker.
                    Emit(a.Hit ? $"The {c.Name} has fled by going east." : $"The {c.Name} has just passed on.",
                        neutral: true);
                    c.InRoom = false;
                    break;
                case ActKind.PlayerDies when c!.InRoom && c.Engaged && !Unseen(c):
                    Emit($"The {c.Name} has killed you.");
                    Dead = true;
                    break;
                case ActKind.NonCombat:
                    foreach (var text in NonCombatLines(c!, a.Who))
                        Emit(text, neutral: true);
                    break;
                case ActKind.Frame:
                    // The truth at the prompt: whether any creature is still fighting the player.
                    Steps.Add((World.Any(w => w.Engaged) && !Dead ? "frame engaged" : "frame over",
                        new StyledLine([new StyledSpan("> ", TextStyle.Default)], isPartial: true)));
                    Transcript.Add("<prompt>");
                    break;
            }
        }
    }

    private static List<Act> Generate(int seed)
    {
        var rng = new Random(seed);
        var kinds = Enum.GetValues<ActKind>();
        var acts = new List<Act>();
        var length = rng.Next(12, 48);
        for (var i = 0; i < length; i++)
        {
            // Swings and starts dominate: they are what the rules are about.
            var roll = rng.Next(100);
            var kind = roll switch
            {
                < 22 => ActKind.CreatureSwings,
                < 34 => ActKind.PlayerSwings,
                < 46 => ActKind.CreatureStarts,
                < 52 => ActKind.PlayerStarts,
                < 60 => ActKind.Frame,
                _ => kinds[rng.Next(kinds.Length)],
            };
            acts.Add(new Act(kind, rng.Next(Cast.Length), rng.Next(3) != 0));
        }
        return acts;
    }

    private sealed record Outcome(string? Failure, string Transcript);

    private static Outcome Run(IReadOnlyList<Act> acts, int seed)
    {
        var render = new Render();
        foreach (var a in acts)
        {
            render.Apply(a);
            var engaged = render.World.Where(w => w.Engaged).Select(w => w.Name).ToHashSet();
            while (render.Truth.Count < render.Steps.Count)
                render.Truth.Add(engaged);
        }

        var tracker = new CombatTracker();
        var swings = new List<CombatEvent>();
        var starts = 0;
        var unseenChanges = 0;
        var counted = new List<string>();
        tracker.EventOccurred += e =>
        {
            if (e.Kind == CombatEventKind.FightStart)
                starts++;
            if (e.Kind == CombatEventKind.UnseenRowEnded && e.Outcome != FightOutcome.FateUnseen)
                counted.Add(e.NpcName!);
            if (e.Kind == CombatEventKind.UnseenChanged)
                unseenChanges++;
            if (e.Kind is CombatEventKind.Hit or CombatEventKind.Miss
                or CombatEventKind.HitByNpc or CombatEventKind.MissByNpc)
                swings.Add(e);
        };

        var transcript = string.Join("\n", render.Transcript.Select((t, i) => $"  {i,2}: {t}"));
        var at = 0;
        for (var i = 0; i < render.Steps.Count; i++)
        {
            var (step, line) = render.Steps[i];
            counted.Clear();
            var when = T0.AddSeconds(at++);
            if (step == "dark")
                tracker.NoteCannotSee(true, when, "fuzz");
            else if (step == "light")
                tracker.NoteCannotSee(false, when, "fuzz");
            else
            {
                var inCombat = tracker.InCombat;
                var changes = unseenChanges;
                tracker.Observe(line!, when);
                if (step == "neutral" && (tracker.InCombat != inCombat || unseenChanges != changes))
                    return new Outcome($"'{line!.PlainText}' changed the tracker's state", transcript);
                // Closing: every participant has had its own-fight ending (or the player left or
                // died), so the encounter is over by the prompt; and while one is still fighting, it
                // is open.
                if (step == "frame over" && tracker.InCombat)
                    return new Outcome($"still in combat at prompt {at - 1} with nobody fighting", transcript);
                if (step == "frame engaged" && !tracker.InCombat)
                    return new Outcome($"out of combat at prompt {at - 1} with a creature still fighting", transcript);
            }
            // Counting never concludes a creature the truth says is still fighting.
            foreach (var name in counted)
                if (render.Truth[i].Contains(name))
                    return new Outcome($"{name} concluded by counting at step {i} while still fighting", transcript);
        }

        string? failure = null;
        var expected = render.Truths;
        if (swings.Count != expected.Count)
            failure = $"{expected.Count} swing lines produced {swings.Count} swing events";
        else if (starts != render.Starts)
            failure = $"{render.Starts} start lines produced {starts} fight starts";
        for (var i = 0; failure is null && i < swings.Count; i++)
        {
            var name = swings[i].NpcName;
            var (truth, anonymous) = expected[i];
            var want = anonymous ? AnonymousOpponent.Group : truth;
            if (!string.Equals(name, want, StringComparison.Ordinal))
                failure = $"swing {i} credited to '{name}', thrown by '{truth}'{(anonymous ? " unseen" : "")}";
        }
        return new Outcome(failure, transcript);
    }

    private static List<Act> Shrink(List<Act> acts, int seed)
    {
        var current = acts;
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < current.Count; i++)
            {
                var without = current.Where((_, ix) => ix != i).ToList();
                if (Run(without, seed).Failure is not null)
                {
                    current = without;
                    changed = true;
                    break;
                }
            }
        }
        return current;
    }

    public static IEnumerable<object[]> Seeds => Enumerable.Range(1, 5000).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomFight_IsSoundConservingClosingAndNeutral(int seed)
    {
        var acts = Generate(seed);
        var outcome = Run(acts, seed);
        if (outcome.Failure is null)
            return;
        var small = Shrink(acts, seed);
        var shrunk = Run(small, seed);
        Assert.Fail($"seed {seed}: {shrunk.Failure}\nshrunk transcript:\n{shrunk.Transcript}");
    }
}
