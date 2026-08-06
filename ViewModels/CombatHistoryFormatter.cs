using System.Globalization;
using MudSharp.Combat;

namespace Mucka.ViewModels;

/// <summary>
/// Builds the clog window's readout as styled lines (see <see cref="ClogLine"/>).
///
/// <para>Layout intent, per the user's review of the first pass: labels were eating most of the
/// width, and the panel was full of things that do not help mid-swing (absolute Sta/Str/Dex/Mag,
/// carry, level, games played, a redundant "Combat" heading under a window already titled Clog).
/// Colour now carries what labels used to - friendly for the player's side, hostile for the NPC's -
/// and only stat DEFICITS are shown, since a penalty is actionable where a raw number is not.</para>
///
/// <para>Second pass, per the user's stated goal for this window (survivability assistance during
/// combat, plus surfacing hidden combat factors): the survivability read - outlook verdict, die/kill
/// projection, current stamina - is promoted to directly under the headline rather than buried below
/// the exchange table, since it is the whole point of the window. DPS gave way to damage-per-LANDED-
/// hit throughout ("how hard is it hitting", which matters more than a rate in MUD2's slow,
/// high-miss-rate fights), and a bounded recent-hits strip was added so the miss rhythm and hit
/// magnitude are visible at a glance for the fight actually on screen.</para>
///
/// <para>Pure string/span building with no MAUI types, so it is linked into mudsharp.Tests.</para>
/// </summary>
internal static class CombatHistoryFormatter
{
    private const string NoValue = "--";

    /// <summary>Assembles the whole readout, top to bottom.</summary>
    public static List<ClogLine> Build(
        CombatEncounterSnapshot snapshot,
        CombatStatDeficits deficits,
        CombatHistoryContext history,
        SessionCombatTotals? session = null)
    {
        session ??= SessionCombatTotals.Empty;
        var lines = new List<ClogLine>(28);
        if (!snapshot.HasEncounter)
        {
            AppendSessionTotals(lines, session);
            return lines;
        }

        // Result banner first: once a fight is over the outcome is the thing you want to see, and it
        // now stays on screen until explicitly cleared rather than vanishing after 8 seconds.
        AppendResultBanner(lines, snapshot);
        AppendHeadline(lines, snapshot);
        // Survivability - outlook + stamina - sits directly under the headline, ahead of even the
        // participant lines: it is the window's whole reason for existing (see class remarks), and
        // burying it below the exchange/history tables meant the signal that actually matters most
        // was the last thing on screen instead of the first.
        AppendSurvivability(lines, snapshot, deficits, history);
        AppendParticipants(lines, snapshot);
        AppendExchange(lines, snapshot);
        AppendRecentHits(lines, snapshot);
        AppendDeficits(lines, deficits);

        if (history.HasAnything)
        {
            lines.Add(ClogLine.Blank);
            AppendHistoryHeading(lines, history);
            AppendFleeRisk(lines, history);
            AppendHistoryComparison(lines, snapshot, history);
            AppendWeaponTable(lines, snapshot, history);
        }

        if (!snapshot.InCombat && session.HasAnything)
        {
            lines.Add(ClogLine.Blank);
            AppendSessionTotals(lines, session);
        }

        return lines;
    }

    /// <summary>"killed zombie0  0:27  65.5 dealt" once the encounter has closed. Nothing while the
    /// fight is still live - the headline already covers that, and a verdict mid-fight would be a
    /// lie.</summary>
    private static void AppendResultBanner(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        if (snapshot.InCombat)
            return;

        var decisive = ResultOf(snapshot);
        if (decisive is null)
            return;

        // ASCII glyphs, each exactly one cell wide in Cascadia Mono (unlike the Unicode marks these
        // replaced, which were not fixed-advance and silently broke this column's alignment).
        var (glyph, verb, tone) = decisive.Outcome switch
        {
            FightOutcome.Killed => ("+", "killed", ClogTone.Good),
            FightOutcome.KilledByNpc => ("x", "killed by", ClogTone.Hostile),
            FightOutcome.NpcFled => (">", "fled:", ClogTone.Warn),
            FightOutcome.YouFled => ("<", "you fled", ClogTone.Warn),
            FightOutcome.Withdrawn => ("-", "withdrew", ClogTone.Dim),
            _ => (".", "open", ClogTone.Dim),
        };

        // Verdict and target only. The duration lives on this fight's participant line and the damage
        // in the exchange table below, so repeating either here is what made the same number appear
        // six times in one panel.
        lines.Add(ClogLine.Of(
            new ClogSpan($"{glyph} {verb} ", tone),
            new ClogSpan(decisive.NpcName, ClogTone.Hostile)));
        lines.Add(ClogLine.Blank);
    }

    /// <summary>The fight that decided the encounter: a player death outranks everything, then a
    /// kill, else the last resolved fight.</summary>
    private static FightSnapshot? ResultOf(CombatEncounterSnapshot snapshot)
    {
        FightSnapshot? fallback = null;
        foreach (var fight in snapshot.Fights)
        {
            if (fight.Outcome == FightOutcome.KilledByNpc)
                return fight;
            if (fight.IsResolved)
                fallback = fight.Outcome == FightOutcome.Killed ? fight : fallback ?? fight;
        }

        return fallback;
    }

    /// <summary>Between-fights view: what this session has amounted to, in the same terms the live
    /// rows use so the panel reads as one thing rather than two.</summary>
    private static void AppendSessionTotals(List<ClogLine> lines, SessionCombatTotals session)
    {
        if (!session.HasAnything)
            return;

        // Two labelled lines, not one line of abbreviations: the old form squeezed fights/encounters
        // to "12f/3e", spelled out only "fled", then tacked on two bare unlabelled numbers and a bare
        // duration - the user called this out directly as inconsistent. Every number here now says
        // what it is, in the same units ("dealt"/"taken") the live exchange table already uses.
        var top = new List<ClogSpan>(5)
        {
            new("session  ", ClogTone.Heading),
            new($"{session.Fights} {(session.Fights == 1 ? "fight" : "fights")}", ClogTone.Value),
            new($"  {session.Kills} killed", ClogTone.Good),
        };
        if (session.Deaths > 0)
            top.Add(new ClogSpan($"  {session.Deaths} died", ClogTone.Hostile));
        if (session.NpcFled > 0)
            top.Add(new ClogSpan($"  {session.NpcFled} fled", ClogTone.Warn));
        lines.Add(new ClogLine(top));

        lines.Add(ClogLine.Of(
            new ClogSpan("         ", ClogTone.Dim),
            new ClogSpan($"{Num(session.DamageDealt)} dealt", ClogTone.Friendly),
            new ClogSpan(" / ", ClogTone.Dim),
            new ClogSpan($"{Num(session.DamageTaken)} taken", ClogTone.Hostile),
            new ClogSpan($"  {Duration(session.TimeInCombat.TotalSeconds)}", ClogTone.Dim)));
    }

    /// <summary>Whether this opponent is likely to run. Only rendered at or above a coin flip, since
    /// below that it is not decision-changing and would just be another always-present row.</summary>
    private static void AppendFleeRisk(List<ClogLine> lines, CombatHistoryContext history)
    {
        var summary = history.Primary;
        if (summary.FleeRate is not double rate || rate < 0.5)
            return;

        lines.Add(ClogLine.Of(
            new ClogSpan("flees", ClogTone.Warn),
            new ClogSpan($"   {Percent(rate)} of the time", ClogTone.Warn),
            new ClogSpan($" ({summary.NpcFled}/{summary.FightCount})", ClogTone.Dim)));
    }

    /// <summary>
    /// The survivability block: the outlook verdict ("am I going to die first"), the projected
    /// die/kill times, and current stamina - the window's whole reason for existing, per the user's
    /// stated goal. Promoted to sit directly under the headline (see <see cref="Build"/>).
    ///
    /// <para>Independently gated: the outlook row stays silent until <see cref="CombatOutlook"/> has
    /// enough to say something honest (an early guess is worse than none), while the stamina row
    /// shows whenever a reading exists at all, since "how much stamina do I have right now" needs no
    /// projection to be true. Neither half renders an empty block when idle - see the joint guard
    /// below.</para>
    /// </summary>
    private static void AppendSurvivability(
        List<ClogLine> lines,
        CombatEncounterSnapshot snapshot,
        CombatStatDeficits deficits,
        CombatHistoryContext history)
    {
        if (!snapshot.InCombat)
            return;

        var primary = PrimaryFight(snapshot);
        var outlook = primary is null
            ? CombatOutlook.Unknown
            : CombatOutlook.Project(
                primary.Duration.TotalSeconds,
                primary.ApproxDamageDone,
                primary.ApproxDamageTaken,
                primary.YouHits,
                primary.TheyHits,
                deficits.StaminaCurrent,
                history.Primary.EstimatedStaminaPool);

        var hasOutlook = outlook.Verdict != OutlookVerdict.Unknown;
        var hasStamina = deficits.StaminaCurrent is not null;
        if (!hasOutlook && !hasStamina)
            return;

        lines.Add(ClogLine.Blank);

        if (hasOutlook)
        {
            var (text, tone) = outlook.Verdict switch
            {
                OutlookVerdict.Winning => ("winning", ClogTone.Good),
                OutlookVerdict.Losing => ("LOSING", ClogTone.Hostile),
                OutlookVerdict.Even => ("too close", ClogTone.Warn),
                _ => ("unhurt so far", ClogTone.Good),
            };

            var spans = new List<ClogSpan>(4) { new("  ", ClogTone.Dim), new(text, tone) };

            // Die before kill - the scarier number leads, since this block exists to answer "am I
            // going to die" before anything else.
            if (outlook.SecondsToKill is double kill)
            {
                spans.Add(new ClogSpan(
                    outlook.SecondsToDie is double die ? $"   die {Duration(die)}" : "   die --",
                    ClogTone.Dim));
                spans.Add(new ClogSpan($"   kill {Duration(kill)}", ClogTone.Dim));
            }

            lines.Add(new ClogLine(spans));
        }

        if (hasStamina)
        {
            var current = deficits.StaminaCurrent!.Value;
            var staText = deficits.StaminaMax is int max
                ? $"{current}/{max}"
                : current.ToString(CultureInfo.InvariantCulture);
            lines.Add(ClogLine.Of(
                new ClogSpan("  sta ", ClogTone.Dim),
                new ClogSpan(staText, StaminaTone(current, deficits.StaminaMax))));
        }
    }

    /// <summary>Hostile once stamina is critically low, warn once it is getting there, otherwise
    /// plain - so the number that most directly answers "how much longer can I take this" carries
    /// its own urgency rather than reading as inert as everything else on the line.</summary>
    private static ClogTone StaminaTone(int current, int? max)
    {
        if (max is not int m || m <= 0)
            return ClogTone.Value;

        var fraction = current / (double)m;
        if (fraction <= 0.25)
            return ClogTone.Hostile;
        return fraction <= 0.5 ? ClogTone.Warn : ClogTone.Value;
    }

    /// <summary>"{weapon} vs {live targets}, {dead targets}    {elapsed}". Dead targets sort to the
    /// right of the list and render struck through, so a glance says both who is left and what has
    /// already gone down without spending a line on either.</summary>
    private static void AppendHeadline(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        var spans = new List<ClogSpan>(8)
        {
            new(string.IsNullOrWhiteSpace(snapshot.CurrentWeapon) ? "unarmed" : DisplayName(snapshot.CurrentWeapon),
                ClogTone.Friendly),
            new(" vs ", ClogTone.Dim),
        };

        var written = 0;
        // Live first, then resolved - the ordering IS the information here.
        foreach (var fight in OrderedTargets(snapshot.Fights))
        {
            if (written++ > 0)
                spans.Add(new ClogSpan(", ", ClogTone.Dim));
            spans.Add(new ClogSpan(fight.NpcName, ClogTone.Hostile, Strike: fight.IsResolved));
        }

        if (written == 0)
            spans.Add(new ClogSpan("--", ClogTone.Dim));

        // ENCOUNTER clock here; each participant carries its own fight clock on its own line below.
        // The two differ - an encounter can run on past a kill (grace window, a second attacker), so
        // collapsing them into one number would misreport both.
        spans.Add(new ClogSpan("  enc " + Duration(snapshot.Duration.TotalSeconds), ClogTone.Dim));
        lines.Add(new ClogLine(spans));
    }

    /// <summary>
    /// Maximum participant rows rendered per encounter. Each row is its own native span rebuild in
    /// the clog window (see ClogPage.Render), and an 11-rat pack fight puts up to 22 lines (a
    /// participant line plus a possible "armed with" line each) on screen every time the readout
    /// refreshes - nobody reads eleven rat names mid-swing anyway. Five keeps the current melee
    /// visible at a glance; <see cref="OrderedTargets"/> already sorts live fights ahead of
    /// resolved ones, so capping AFTER that ordering means a truncated pack fight always keeps
    /// whoever is still swinging and drops finished fights first.
    /// </summary>
    private const int MaxParticipantRows = 5;

    /// <summary>One line per NPC engaged this encounter, carrying that fight's own duration and how it
    /// ended, plus a second indented line naming the NPC's own weapon once one is confirmed. This is
    /// where per-fight timing lives, as distinct from the encounter clock above.</summary>
    private static void AppendParticipants(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        if (snapshot.Fights.Count == 0)
            return;

        var ordered = OrderedTargets(snapshot.Fights).ToList();
        var shown = ordered.Count > MaxParticipantRows ? ordered.Take(MaxParticipantRows).ToList() : ordered;

        var width = Math.Min(shown.Max(f => f.NpcName.Length), 14);
        foreach (var fight in shown)
        {
            // Name and its column padding are separate spans so the strikethrough covers the NAME only
            // - struck padding renders as a stray dash trailing off into empty space.
            var name = Truncate(fight.NpcName, width);
            lines.Add(ClogLine.Of(
                new ClogSpan(" ", ClogTone.Dim),
                new ClogSpan(name, ClogTone.Hostile, Strike: fight.IsResolved),
                new ClogSpan(new string(' ', width - name.Length), ClogTone.Dim),
                new ClogSpan($" {Duration(fight.Duration.TotalSeconds),5}", ClogTone.Value),
                // This fight's own damage dealt/taken. The exchange table above is encounter-wide, so
                // without this a pack fight could not say which target absorbed what.
                new ClogSpan($" {Num(fight.ApproxDamageDone),6}", ClogTone.Friendly),
                new ClogSpan($"/{Num(fight.ApproxDamageTaken)}", ClogTone.Hostile),
                new ClogSpan($"  {DescribeOutcome(fight)}", OutcomeTone(fight.Outcome))));

            // The NPC's own weapon, once one has been confirmed. Most NPCs never announce one
            // (fists/claws/bite), so this line is the exception rather than the rule - but it is
            // exactly the missing context for a damage jump that has no other explanation, since
            // MUD2 gives an NPC picking up a weapon mid-fight no other visible signal at all.
            if (!string.IsNullOrWhiteSpace(fight.NpcWeapon))
            {
                lines.Add(ClogLine.Of(
                    new ClogSpan("   armed with ", ClogTone.Dim),
                    new ClogSpan(DisplayName(fight.NpcWeapon), ClogTone.Hostile)));
            }
        }

        var hidden = ordered.Count - shown.Count;
        if (hidden > 0)
        {
            // Resolved fights are always the ones missing here (see MaxParticipantRows) - unless
            // the pack itself already outnumbers the cap while still live, in which case there was
            // never room for all of them regardless of outcome.
            lines.Add(ClogLine.Of(new ClogSpan($"   and {hidden} more", ClogTone.Dim)));
        }
    }

    private static string DescribeOutcome(FightSnapshot fight) => fight.Outcome switch
    {
        FightOutcome.Killed => "killed",
        FightOutcome.KilledByNpc => "killed you",
        FightOutcome.NpcFled => "fled",
        FightOutcome.YouFled => "you fled",
        FightOutcome.Withdrawn => "withdrew",
        _ => "live",
    };

    private static ClogTone OutcomeTone(FightOutcome outcome) => outcome switch
    {
        FightOutcome.Killed => ClogTone.Good,
        FightOutcome.KilledByNpc => ClogTone.Hostile,
        FightOutcome.NpcFled or FightOutcome.YouFled => ClogTone.Warn,
        _ => ClogTone.Dim,
    };

    private static IEnumerable<FightSnapshot> OrderedTargets(IReadOnlyList<FightSnapshot> fights)
    {
        foreach (var fight in fights)
        {
            if (!fight.IsResolved)
                yield return fight;
        }

        foreach (var fight in fights)
        {
            if (fight.IsResolved)
                yield return fight;
        }
    }

    /// <summary>
    /// The exchange as a two-column table, player against opponent, one metric per row.
    ///
    /// <para>Replaced the previous one-line-per-side form: with the values running horizontally there
    /// was no way to compare like against like without counting columns, and the row labels had to be
    /// re-read on every line. Vertically, each metric sits on one row and the two numbers are directly
    /// above and below each other.</para>
    /// </summary>
    private static void AppendExchange(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        lines.Add(ClogLine.Of(
            new ClogSpan($"{string.Empty,-9}", ClogTone.Dim),
            new ClogSpan($"{"you",7}", ClogTone.Friendly),
            new ClogSpan($"{"them",7}", ClogTone.Hostile)));

        AppendExchangeRow(lines, "hit/miss",
            $"{snapshot.YouHits}/{snapshot.YouMisses}",
            $"{snapshot.TheyHits}/{snapshot.TheyMisses}");
        AppendExchangeRow(lines, "hit%",
            Percent(snapshot.YouHits + snapshot.YouMisses == 0 ? null : snapshot.YouHitRate),
            Percent(snapshot.TheyHits + snapshot.TheyMisses == 0 ? null : snapshot.TheyHitRate));
        AppendExchangeRow(lines, "damage", Num(snapshot.ApproxDamageDone), Num(snapshot.ApproxDamageTaken));

        // "per hit" replaces the old "rate" (x.x/s) row. The user specifically flagged DPS as the
        // wrong metric here: MUD2 fights are slow with a high miss rate, so a per-second rate blurs
        // together two different questions - how often blows land (already covered by hit% above)
        // and how hard they land when they do. Damage per LANDED blow isolates that second question,
        // and matches the units the history comparison and weapon table already report. Guarded
        // against zero landed hits rather than dividing by zero.
        AppendExchangeRow(lines, "per hit",
            Num(snapshot.YouHits == 0 ? null : snapshot.ApproxDamageDone / snapshot.YouHits),
            Num(snapshot.TheyHits == 0 ? null : snapshot.ApproxDamageTaken / snapshot.TheyHits));
    }

    private static void AppendExchangeRow(List<ClogLine> lines, string label, string mine, string theirs)
        => lines.Add(ClogLine.Of(
            new ClogSpan($"{label,-9}", ClogTone.Dim),
            new ClogSpan($"{mine,7}", ClogTone.Friendly),
            new ClogSpan($"{theirs,7}", ClogTone.Hostile)));

    /// <summary>
    /// The last few swings per side, newest on the right so the strip reads as a timeline - added
    /// because DPS and even the exchange table's totals cannot show "how hard is it hitting" and the
    /// miss rhythm the way a sequence of individual swings can. PRIMARY fight only: this is a live-
    /// combat aid for the fight actually in front of the player, not a log of every target this
    /// encounter, so a pack fight does not get one strip per NPC.
    /// </summary>
    private static void AppendRecentHits(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        var primary = PrimaryFight(snapshot);
        if (primary is null || (primary.RecentYourSwings.Count == 0 && primary.RecentTheirSwings.Count == 0))
            return;

        lines.Add(RecentHitsRow("recent", "you", primary.RecentYourSwings, ClogTone.Friendly));
        lines.Add(RecentHitsRow(string.Empty, "them", primary.RecentTheirSwings, ClogTone.Hostile));
    }

    private static ClogLine RecentHitsRow(string label, string side, IReadOnlyList<SwingOutcome> swings, ClogTone tone)
    {
        var spans = new List<ClogSpan>(2 + FightAccumulator.RecentSwingCapacity)
        {
            new($"{label,-8}", ClogTone.Dim),
            new($"{side,-5}", tone),
        };

        // Left-pad with empty columns so a fight that has not yet built up a full ring still lines
        // its newest swing up under the SAME column the strip settles into once full, rather than
        // visibly drifting rightward as swings accumulate.
        for (var i = swings.Count; i < FightAccumulator.RecentSwingCapacity; i++)
            spans.Add(new ClogSpan("    ", ClogTone.Dim));

        foreach (var swing in swings)
            spans.Add(new ClogSpan(swing.IsHit ? $"{swing.Damage,4:0}" : $"{"-",4}", tone));

        return new ClogLine(spans);
    }

    /// <summary>Only rendered when something is actually costing the player stats. A line that always
    /// reads "0" is a line that stops being read.</summary>
    private static void AppendDeficits(List<ClogLine> lines, CombatStatDeficits deficits)
    {
        if (!deficits.HasStatDelta && !deficits.HasLoad)
            return;

        var spans = new List<ClogSpan>(6) { new("load", ClogTone.Dim) };

        if (deficits.StrengthDelta is int strength && strength != 0)
            spans.Add(new ClogSpan($" str {Signed(strength)}", strength < 0 ? ClogTone.Warn : ClogTone.Good));
        if (deficits.DexterityDelta is int dexterity && dexterity != 0)
            spans.Add(new ClogSpan($" dex {Signed(dexterity)}", dexterity < 0 ? ClogTone.Warn : ClogTone.Good));

        // The cause, and the fix: dropping it is standard practice before a real fight.
        if (deficits.WeightCarriedGrams is int grams && grams > 0)
            spans.Add(new ClogSpan($"  {grams}g", ClogTone.Dim));
        if (deficits.ObjectsCarried is int objects && objects > 0)
            spans.Add(new ClogSpan($" {objects}obj", ClogTone.Dim));

        if (spans.Count > 1)
            lines.Add(new ClogLine(spans));
    }

    /// <summary>Names what the medians below describe, and says when the instance is speaking for
    /// itself rather than borrowing its group's numbers.</summary>
    private static void AppendHistoryHeading(List<ClogLine> lines, CombatHistoryContext history)
    {
        if (history.PreferInstance)
        {
            lines.Add(ClogLine.Of(
                new ClogSpan(history.InstanceName, ClogTone.Heading),
                new ClogSpan($" {Fights(history.Instance.FightCount)}", ClogTone.Dim),
                new ClogSpan($"  ({history.GroupName} {history.Group.FightCount})", ClogTone.Dim)));
            return;
        }

        var detail = history.Group.SampleSize == history.Group.FightCount
            ? string.Empty
            : $", {history.Group.SampleSize} detailed";
        lines.Add(ClogLine.Of(
            new ClogSpan(history.GroupName, ClogTone.Heading),
            new ClogSpan($" {Fights(history.Group.FightCount)}{detail}", ClogTone.Dim)));
    }

    private static void AppendHistoryComparison(
        List<ClogLine> lines, CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var summary = history.Primary;
        var primary = PrimaryFight(snapshot);

        // "usual" rather than "med": the figure IS a median, but in a game with magic "med" reads as
        // medium/meditate, and the user flagged it as genuinely ambiguous. "usual" says what the
        // column is FOR without the collision. (Still a median, not a mean - one wandered-off fight
        // is a large fraction of a single-digit sample and would wreck an average.) Standardised on
        // this term everywhere the same concept shows up - see AppendWeaponTable, which used to say
        // "hist" for the identical column.
        lines.Add(ClogLine.Of(new ClogSpan($"{string.Empty,-8}{"now",7}{"usual",8}", ClogTone.Dim)));

        AppendPair(lines, "dealt", primary is null ? null : primary.ApproxDamageDone, summary.MedianDamageDone, Num);
        AppendPair(lines, "taken", primary is null ? null : primary.ApproxDamageTaken, summary.MedianDamageTaken, Num);
        AppendPair(lines, "you hit", primary is null ? null : HitRate(primary.YouHits, primary.YouMisses),
            summary.MedianYouHitRate, Percent);
        AppendPair(lines, "they hit", primary is null ? null : HitRate(primary.TheyHits, primary.TheyMisses),
            summary.MedianTheyHitRate, Percent);
        AppendPair(lines, "time", primary?.Duration.TotalSeconds, summary.MedianDurationSeconds, Duration);

        lines.Add(ClogLine.Of(new ClogSpan(OutcomeTally(summary), ClogTone.Dim)));

        // Labelled "to kill" rather than "pool": the user reported not understanding what "pool"
        // meant, and the plain reading of the number is "how much damage it usually takes to put one
        // of these down". Compressed to the table's own "[Nx]" sample-count idiom (already used by
        // the weapon table below) rather than the old spelled-out ", dmg, over N kills" prose - same
        // information, same idiom the rest of the panel already uses for "how many samples".
        if (summary.EstimatedStaminaPool is double pool)
        {
            lines.Add(ClogLine.Of(
                new ClogSpan($"{"to kill",-8}", ClogTone.Dim),
                new ClogSpan($"{"~" + Num(pool),7}", ClogTone.Hostile),
                new ClogSpan($"  [{summary.Kills}x]", ClogTone.Dim)));
        }
        else
        {
            lines.Add(ClogLine.Of(
                new ClogSpan($"{"to kill",-8}", ClogTone.Dim),
                new ClogSpan($"{NoValue,7}", ClogTone.Dim),
                new ClogSpan("  never killed one", ClogTone.Dim)));
        }
    }

    private static void AppendPair(
        List<ClogLine> lines, string label, double? now, double? median, Func<double?, string> format)
    {
        // Skip entirely when neither side has anything to say, rather than printing "-- / --".
        if (now is null && median is null)
            return;

        lines.Add(ClogLine.Of(
            new ClogSpan($"{label,-8}", ClogTone.Dim),
            new ClogSpan($"{format(now),7}", ClogTone.Value),
            new ClogSpan($" {format(median),7}", ClogTone.Dim)));
    }

    /// <summary>
    /// Per-weapon table for the NPC's GROUP (never the instance - susceptibility is a property of
    /// the creature type, and the group is where samples accumulate).
    ///
    /// <para>Sorted by the live "now" figure once it is trusted, falling back to the historical
    /// "usual" median otherwise (see the sort's own comment for the trust gate). The weapon currently
    /// in hand is always present even with no history at all, marked and tinted against the best on
    /// record: green when it is beating the best, amber when it is not. That live over/under signal
    /// is the point - it is what makes experimenting with weapons mid-fight legible. A "vs all" row
    /// under the current weapon shows its median across EVERY creature on file, not just this NPC's
    /// group, so the reader can tell whether this particular group is unusually kind or harsh to it.</para>
    /// </summary>
    private static void AppendWeaponTable(
        List<ClogLine> lines, CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var current = snapshot.CurrentWeapon;
        var rows = new List<(string Weapon, double? Hist, int Count, bool IsCurrent)>();

        double? best = null;
        foreach (var entry in history.ByWeapon)
        {
            if (entry.Summary.MedianDamagePerHit is double perHit && (best is null || perHit > best))
                best = perHit;

            // "unarmed" matches the headline's own wording for the same state - the bucket's raw
            // storage key ("(none)") read as a data placeholder rather than a real state.
            var label = string.Equals(entry.Weapon, FightHistory.NoWeaponKey, StringComparison.Ordinal)
                ? "unarmed"
                : DisplayName(entry.Weapon);
            // Matching stays on the FULL name (DisplayName is display-only), so "a rusty pick2" in
            // history still matches the same weapon in hand.
            rows.Add((label, entry.Summary.MedianDamagePerHit, entry.Summary.FightCount,
                IsCurrentWeapon(entry.Weapon, current)));
        }

        var haveCurrentRow = rows.Any(row => row.IsCurrent);
        if (!haveCurrentRow && !string.IsNullOrWhiteSpace(current))
            rows.Add((DisplayName(current), null, 0, true));   // first outing with this weapon here

        if (rows.Count == 0)
            return;

        var primary = PrimaryFight(snapshot);
        var liveNow = LivePerHit(snapshot, history);

        // Below this many landed hits THIS fight, the live per-hit figure is too noisy to trust as a
        // SORT key: one lucky or unlucky opening swing would fling the current weapon to the top or
        // bottom of the table, and it would visibly climb/sink back down as more swings land. The row
        // still DISPLAYS the live figure below the gate (see nowText below) - only its RANK falls back
        // to the historical median until this fight has built up enough evidence of its own.
        const int MinHitsForLiveSort = 3;
        var useLiveSort = liveNow is not null && primary is not null && primary.YouHits >= MinHitsForLiveSort;

        // Best first - the live figure once trusted, else the historical median (see gate above).
        // Unmeasured weapons sink to the bottom rather than sorting as zero.
        rows.Sort((a, b) =>
        {
            var aKey = a.IsCurrent && useLiveSort ? liveNow : a.Hist;
            var bKey = b.IsCurrent && useLiveSort ? liveNow : b.Hist;
            if (aKey is null && bKey is null)
                return string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
            if (aKey is null)
                return 1;
            if (bKey is null)
                return -1;
            var byKey = bKey.Value.CompareTo(aKey.Value);
            return byKey != 0 ? byKey : string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
        });

        var width = Math.Min(rows.Max(row => row.Weapon.Length), 15);

        // Header widths are derived from the same column widths the rows use below, so a long weapon
        // name ("croquet mallet") cannot slide the values out from under their labels. "usual" rather
        // than the old "hist" - standardised on the same term AppendHistoryComparison uses for the
        // identical concept, so the reader is not re-learning what a column means twice in one panel.
        lines.Add(ClogLine.Of(
            new ClogSpan(" " + "weapon".PadRight(width), ClogTone.Heading),
            new ClogSpan($" {"now",5} {"usual",6}", ClogTone.Dim)));
        foreach (var row in rows)
        {
            var marker = row.IsCurrent ? ">" : " ";
            var nowText = row.IsCurrent && liveNow is not null ? Num(liveNow) : NoValue;
            var tone = row.IsCurrent ? CurrentWeaponTone(liveNow, best) : ClogTone.Value;

            lines.Add(ClogLine.Of(
                new ClogSpan(marker, ClogTone.Dim),
                new ClogSpan(Truncate(row.Weapon, width).PadRight(width), tone),
                new ClogSpan($" {nowText,5}", tone),
                new ClogSpan($" {(row.Hist is null ? NoValue : Num(row.Hist)),6}", ClogTone.Dim),
                new ClogSpan(row.Count > 0 ? $" [{row.Count}x]" : " [new]", ClogTone.Dim)));

            // The current weapon's record against EVERY creature on file, not just this NPC's
            // group - the group-scoped row above can only say how the weapon does against THIS
            // group; this says whether that group is typical for the weapon at all.
            if (row.IsCurrent && history.CurrentWeaponGlobal.FightCount > 0)
            {
                lines.Add(ClogLine.Of(
                    new ClogSpan("  vs all".PadRight(1 + width), ClogTone.Dim),
                    new ClogSpan($" {string.Empty,5}", ClogTone.Dim),
                    new ClogSpan($" {Num(history.CurrentWeaponGlobal.MedianDamagePerHit),6}", ClogTone.Dim),
                    new ClogSpan($" [{history.CurrentWeaponGlobal.FightCount}x]", ClogTone.Dim)));
            }
        }
    }

    /// <summary>Green when this weapon is currently out-hitting the best on record, amber when it is
    /// falling short, plain when there is nothing yet to compare against.</summary>
    private static ClogTone CurrentWeaponTone(double? liveNow, double? best)
    {
        if (liveNow is null || best is null)
            return ClogTone.Value;
        return liveNow.Value >= best.Value ? ClogTone.Good : ClogTone.Warn;
    }

    /// <summary>Damage per landed blow so far in the current fight, which is what the historical
    /// per-hit figures are comparable against. Taken from the primary fight rather than the whole
    /// encounter so a second target of a different species cannot pollute it.</summary>
    private static double? LivePerHit(CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var primary = PrimaryFight(snapshot);
        if (primary is null || primary.YouHits == 0 || primary.ApproxDamageDone <= 0)
            return null;
        if (history.GroupName.Length > 0
            && !string.Equals(primary.NpcGroup, history.GroupName, StringComparison.OrdinalIgnoreCase))
            return null;
        return primary.ApproxDamageDone / primary.YouHits;
    }

    private static bool IsCurrentWeapon(string weapon, string? current)
        => !string.IsNullOrWhiteSpace(current) && string.Equals(weapon, current, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fight the comparison describes: the first still-unresolved one, falling back to
    /// the first of the encounter so the block survives the post-kill grace window.</summary>
    internal static FightSnapshot? PrimaryFight(CombatEncounterSnapshot snapshot)
    {
        FightSnapshot? first = null;
        foreach (var fight in snapshot.Fights)
        {
            first ??= fight;
            if (!fight.IsResolved)
                return fight;
        }

        return first;
    }

    private static string OutcomeTally(FightHistorySummary summary)
    {
        var parts = new List<string>(5) { $"killed {summary.Kills}/{summary.FightCount}" };
        if (summary.Deaths > 0)
            parts.Add($"died {summary.Deaths}");
        if (summary.NpcFled > 0)
            parts.Add($"it fled {summary.NpcFled}");
        if (summary.YouFled > 0)
            parts.Add($"you fled {summary.YouFled}");
        if (summary.Withdrawn > 0)
            parts.Add($"withdrew {summary.Withdrawn}");
        if (summary.Unresolved > 0)
            parts.Add($"open {summary.Unresolved}");
        return string.Join(", ", parts);
    }

    private static string Fights(int count) => count == 1 ? "1 fight" : $"{count} fights";

    private static double? HitRate(int hits, int misses)
    {
        var attempts = hits + misses;
        return attempts == 0 ? null : hits / (double)attempts;
    }

    private static string Signed(int value)
        => value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static string Num(double? value)
        => value is null ? NoValue : value.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Percent(double? value)
        => value is null
            ? NoValue
            : Math.Round(value.Value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Duration(double? seconds)
    {
        if (seconds is null)
            return NoValue;
        var total = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..width];

    /// <summary>
    /// Shortens a long item name for DISPLAY only, never for recording.
    ///
    /// <para>MUD2 item names carry descriptive prefixes ("a rusty pick2", "the ornate falchion3") but
    /// the trailing token with its instance number is the part that identifies the thing and the part
    /// the player types. So for anything over <see cref="DisplayNameThreshold"/> characters whose last
    /// word ends in a digit, that last word becomes the label - "a rusty pick2" shows as "pick2",
    /// which also stops one long name from widening the whole weapon column.</para>
    ///
    /// <para>Names whose last word does NOT end in a digit are left alone: "croquet mallet" has no
    /// instance number, so "mallet" would be a lossy guess rather than the canonical short form.
    /// FightRecord always stores the full name, so history and the offline pipeline are unaffected.</para>
    /// </summary>
    internal static string DisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        if (trimmed.Length <= DisplayNameThreshold)
            return trimmed;

        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace < 0 || lastSpace == trimmed.Length - 1)
            return trimmed;

        var lastWord = trimmed[(lastSpace + 1)..];
        return char.IsAsciiDigit(lastWord[^1]) ? lastWord : trimmed;
    }

    private const int DisplayNameThreshold = 10;
}
