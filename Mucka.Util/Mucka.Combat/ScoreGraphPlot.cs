using System.Globalization;

namespace Mucka.Combat;

/// <summary>One character's line on the score graph. <see cref="Permadeaths"/> are the instants its
/// persona was wiped - see <see cref="ScoreHistory.Permadeaths"/>.</summary>
public sealed record ScoreGraphSeries(string Label, ScorePoint[] Points, TimeSpanMs[] Sessions, long[]? Permadeaths = null);

/// <summary>What the score panel has asked to see. Pure data; <see cref="ScoreGraphPlot.Build"/> turns
/// it into pixels.</summary>
/// <param name="BucketMs">0 plots every reading; otherwise readings are grouped into buckets of this
/// length, aligned to local time.</param>
/// <param name="Switches">Persona sessions of a selected character that began straight after a
/// different character's.</param>
/// <param name="Zero">The Y axis runs down to zero rather than fitting the visible values.</param>
public sealed record ScoreGraphScene(
    long StartMs,
    long EndMs,
    bool Squeeze,
    long BucketMs,
    IReadOnlyList<ScoreGraphSeries> Series,
    IReadOnlyList<TimeSpanMs> Runs,
    IReadOnlyList<ScoreSwitch> Switches,
    bool Zero = false);

/// <summary>
/// One move of the score as the game made it: consecutive readings stamped in the same instant and
/// moving the same way, merged. Observed: six swamp drops in one frame arrive as six "Persona saved"
/// lines carrying one timestamp, and a gain and a loss in one frame stay two steps, so a fight that
/// won 2,000 and then lost 5,000 reads as both rather than as -3,000.
///
/// <para>Merging is by identical stamp. A frame has been seen to straddle a millisecond (two lines
/// 1 ms apart), and then it is two steps; nothing groups by time window.</para>
/// </summary>
public readonly record struct ScoreStep(long Ms, long Before, long After, int Readings)
{
    public long Change => After - Before;
}

/// <summary>
/// A climb or a drop as it reads on screen: steps of one sign whose places on the time axis follow
/// each other within <see cref="ScoreGraphPlot.MoveGap"/>. Grouped by what the eye sees as one riser,
/// so it adapts to the zoom: a climb at 1 day may be several at 1 hour. <see cref="Stairs"/> are the
/// steps themselves, one reading each, for the hover to show the staircase and itemise a step.
/// </summary>
public readonly record struct PlotMove(int Series, double X0, double X1, double YFrom, double YTo,
    long From, long To, int Steps, long StartMs, long EndMs, IReadOnlyList<PlotReading> Stairs)
{
    public long Change => To - From;
}

/// <summary>A persona wiped at <see cref="X"/>, where its line stood at <see cref="Y"/>.</summary>
public readonly record struct PlotDeath(double X, double Y);

/// <summary>A persona session of series <see cref="Series"/> that began after a different
/// character's - <see cref="From"/>, by name.</summary>
public readonly record struct ScoreSwitch(int Series, TimeSpanMs Session, string From);

/// <summary>What a mark in the strip above the plot stands for.</summary>
public enum PlotMarkKind { Run, Switch }

/// <summary>A mark in the strip above the plot: where a Mucka run or a switched-to persona session
/// began (<see cref="X"/>), where it ended or the window does (<see cref="EndX"/>), and the span it
/// covers. <see cref="Series"/> is the character switched to, or -1 for a run; <see cref="From"/> the
/// character switched from, empty for a run.</summary>
public readonly record struct PlotMark(PlotMarkKind Kind, double X, double EndX, TimeSpanMs Span, int Series, string From);

/// <summary>A point in the plot, in the same units as the width and height it was built for.</summary>
public readonly record struct PlotPoint(double X, double Y);

/// <summary>A negative step: the score read lower than the reading before it. <see cref="YBefore"/>
/// and <see cref="YAfter"/> are the two ends of the drop. <see cref="Radius"/> is zero when a larger
/// drop nearby holds the marker; the drop itself is still drawn. In a bucketed scale a loss is the sum
/// of every drop in one bucket, and both ends sit at the bucket's low. <see cref="Permadeath"/> marks
/// the drop that followed a persona wipe.</summary>
public readonly record struct PlotLoss(int Series, double X, double YBefore, double YAfter, long Drop, double Radius, bool Labelled,
    bool Permadeath);

/// <summary>One reading as the hover readout reports it, at its place in the plot. For a bucket,
/// <see cref="Ms"/>..<see cref="EndMs"/> is the bucket, <see cref="Total"/> its last reading and
/// <see cref="Change"/> the move from the previous bucket's last; for a single reading the two
/// instants are the same and <see cref="Low"/> = <see cref="High"/> = <see cref="Total"/>.
/// <see cref="Gained"/> and <see cref="Lost"/> are the bucket's steps each way, never netted. In the
/// raw scale a reading is one <see cref="ScoreStep"/>; <see cref="Parts"/> are the score lines merged
/// into it, when there was more than one.</summary>
public readonly record struct PlotReading(double X, double Y, long Ms, long EndMs, long Total, long Change, long Low, long High, long Lost,
    long Gained = 0, IReadOnlyList<long>? Parts = null);

/// <summary>One bucket's range in a bucketed scale: how far the score moved inside it.</summary>
public readonly record struct PlotBar(int Series, double X0, double X1, double YHigh, double YLow);

/// <summary>The readings of one time bucket. <see cref="Lost"/> sums every drop that ended in it,
/// including the drop from the previous bucket's last reading to this bucket's first;
/// <see cref="Gained"/> the same for rises.</summary>
public readonly record struct ScoreBucket(long StartMs, long EndMs, long Low, long High, long Close, long Lost, long Gained);

/// <summary>One character's summary over a window: the score it opened at, its range, and every point
/// gained and lost on the way, counted separately.</summary>
public readonly record struct ScoreStats(long Start, long Low, long High, long Gained, long Lost);

/// <summary>
/// The score graph laid out for a given size: every coordinate the view paints, with nothing left
/// for it to compute. Separated from the view so the arithmetic is testable without MAUI.
///
/// <para><b>No averaging.</b> A gain and a loss moments apart can land in one pixel column; a mean or
/// a last-value bucket would draw that column flat and the loss would vanish. Each column keeps its
/// first, lowest, highest and last reading in order (<see cref="Decimate"/>), so the step line still
/// travels the whole range, and every negative <see cref="ScoreStep"/> is also listed in
/// <see cref="Losses"/> and drawn as a drop. Markers are sized by the drop, one per
/// <see cref="MarkerSlot"/>, the largest in it. Climbs and drops are also grouped into
/// <see cref="Moves"/> for the hover to total.</para>
///
/// <para><b>Scale.</b> A bucketed scale draws each bucket as a bar over its range with the line
/// through the last reading in each, and one loss per bucket totalling its drops - so a gain and a
/// loss inside one bucket still show, as a tall bar with a marker under it.</para>
///
/// <para><b>Axis.</b> One character plots the score itself. Several characters plot points gained
/// since the window opened, because scores of different characters can differ by orders of magnitude
/// and one absolute axis would flatten every line but the highest. The Y range fits the visible
/// values unless the scene asks for <see cref="ScoreGraphScene.Zero"/>.</para>
///
/// <para><b>Squeeze.</b> Time outside every selected character's persona sessions is collapsed to
/// <see cref="GapWidth"/> per gap, so a window of mostly offline days shows the play rather than
/// plateaus. Off, the axis is linear in time.</para>
/// </summary>
public sealed class ScoreGraphPlot
{
    public const double LeftMargin = 58;
    /// <summary>Room right of the plot for each line's live-value tag.</summary>
    public const double RightMargin = 64;
    /// <summary>Room above the plot for the run and switch marks.</summary>
    public const double TopMargin = 18;
    public const double BottomMargin = 20;
    /// <summary>The width a squeezed gap is drawn at.</summary>
    public const double GapWidth = 8;
    /// <summary>How many of the largest drops carry a text label.</summary>
    public const int LabelledLosses = 5;
    private const double MinLossRadius = 2.5;
    private const double MaxLossRadius = 8;
    /// <summary>The width within which only the largest drop gets a marker: two of the largest
    /// markers side by side.</summary>
    public const double MarkerSlot = 2 * MaxLossRadius;
    private const double MinXTickSpacing = 64;
    private static readonly double[] NiceSteps = [1, 2, 2.5, 5, 10];
    private static readonly int[] HourSteps = [1, 3, 6, 12, 24];
    private const double YTickSpacing = 45;
    /// <summary>Steps of one sign closer than this on the time axis read as one climb or drop.</summary>
    public const double MoveGap = 6;

    public required double Left { get; init; }
    public required double Top { get; init; }
    public required double Right { get; init; }
    public required double Bottom { get; init; }
    /// <summary>True when the Y axis is points gained since the window opened rather than the score.</summary>
    public required bool GainMode { get; init; }
    /// <summary>Per series, the vertices of its step line, left to right.</summary>
    public required IReadOnlyList<IReadOnlyList<PlotPoint>> Lines { get; init; }
    /// <summary>Per series, every reading (or bucket) in the window, left to right, for the hover
    /// readout and the live-value tag. Undecimated.</summary>
    public required IReadOnlyList<IReadOnlyList<PlotReading>> Readings { get; init; }
    public required IReadOnlyList<PlotLoss> Losses { get; init; }
    /// <summary>Every climb and drop, per series left to right - see <see cref="PlotMove"/>.</summary>
    public required IReadOnlyList<PlotMove> Moves { get; init; }
    /// <summary>Persona wipes inside the window.</summary>
    public required IReadOnlyList<PlotDeath> Deaths { get; init; }
    public required IReadOnlyList<(double Y, long Value)> YTicks { get; init; }
    public required IReadOnlyList<(double X, string Label)> XTicks { get; init; }
    /// <summary>Per bucket, its range; empty when every reading is plotted.</summary>
    public required IReadOnlyList<PlotBar> Bars { get; init; }
    /// <summary>The run and switch marks inside the window, left to right.</summary>
    public required IReadOnlyList<PlotMark> Marks { get; init; }
    /// <summary>Per series, the readings in the window as the game printed them - see
    /// <see cref="Window"/>. What <see cref="NetChange"/> measures, whatever the scale.</summary>
    public required IReadOnlyList<ScorePoint[]> Points { get; init; }
    /// <summary>Each selected persona session's extent, for the band along the time axis.</summary>
    public required IReadOnlyList<(double X0, double X1)> SessionBands { get; init; }
    /// <summary>The squeezed-out gaps, empty when squeeze is off.</summary>
    public required IReadOnlyList<(double X0, double X1)> Gaps { get; init; }

    public static ScoreGraphPlot Build(ScoreGraphScene scene, double width, double height, TimeZoneInfo zone)
    {
        var left = LeftMargin;
        var top = TopMargin;
        var right = Math.Max(left + 1, width - RightMargin);
        var bottom = Math.Max(top + 1, height - BottomMargin);

        var sessions = scene.Series.SelectMany(s => s.Sessions).ToList();
        var axis = scene.Squeeze
            ? ScoreTimeAxis.Squeezed(scene.StartMs, scene.EndMs, sessions, left, right - left, GapWidth)
            : ScoreTimeAxis.Linear(scene.StartMs, scene.EndMs, left, right - left);

        var gainMode = scene.Series.Count > 1;
        var windows = scene.Series.Select(s => Window(s.Points, scene.StartMs, scene.EndMs)).ToList();
        var values = windows.Select(w => gainMode && w.Length > 0
            ? w.Select(p => p with { Total = p.Total - w[0].Total }).ToArray()
            : w).ToList();

        var all = values.SelectMany(v => v).Select(p => p.Total).ToList();
        var (lo, hi) = all.Count == 0 ? (0L, 1L) : (all.Min(), all.Max());
        if (scene.Zero) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        if (hi == lo) { lo -= 1; hi += 1; }
        var pad = (hi - lo) * 0.05;
        // A zero axis sits on zero, with no margin below it for values that cannot be there.
        var yMin = scene.Zero && lo == 0 ? 0 : lo - pad;
        var yMax = hi + pad;
        // A taller plot earns more gridlines: one per YTickSpacing of height, never fewer than four.
        var ticks = NiceTicks(lo, hi, Math.Max(4, (int)((bottom - top) / YTickSpacing)));
        double Y(long v) => bottom - (v - yMin) * (bottom - top) / (yMax - yMin);

        var lines = new List<IReadOnlyList<PlotPoint>>();
        var readings = new List<IReadOnlyList<PlotReading>>();
        var bars = new List<PlotBar>();
        var raw = new List<PlotLoss>();
        var moves = new List<PlotMove>();
        var deaths = new List<PlotDeath>();
        for (var s = 0; s < values.Count; s++)
        {
            // Readings carry the score as the game printed it; only Y follows the axis mode, through
            // the shift (the window's opening score in gain mode, else zero).
            var pts = values[s];
            var real = windows[s];
            var shift = pts.Length == 0 ? 0 : real[0].Total - pts[0].Total;
            double YReal(long total) => Y(total - shift);
            var frames = FrameSteps(real);
            var steps = frames.Select(f => f.Step).ToList();

            // The drop after each wipe, and the wipe itself where the line stood.
            // The wipe's drop is looked for from the wiped login's last frame (its reset line may be
            // stamped in the same frame as the wipe) up to the start of the login after the next one:
            // a drop any later belongs to some other session and is not the wipe's.
            var wipedAt = new HashSet<long>();
            var starts = scene.Series[s].Sessions.Select(x => x.StartMs).Order().ToArray();
            foreach (var ms in scene.Series[s].Permadeaths ?? [])
            {
                if (ms < scene.StartMs || ms > scene.EndMs || real.Length == 0) continue;
                var held = real.LastOrDefault(p => p.Ms <= ms);
                var lastFrame = held == default ? long.MinValue : held.Ms;
                deaths.Add(new PlotDeath(axis.Map(ms), YReal(held == default ? real[0].Total : held.Total)));
                var later = starts.Where(t => t > ms).Take(2).ToArray();
                var until = later.Length == 2 ? later[1] : long.MaxValue;
                if (steps.FirstOrDefault(st => st.Change < 0 && st.Ms >= lastFrame && st.Ms < until) is { Readings: > 0 } drop)
                    wipedAt.Add(drop.Ms);
            }

            // Climbs follow the raw line; a bucketed scale reports its buckets' gains and losses instead.
            if (scene.BucketMs <= 0)
                moves.AddRange(GroupMoves(s, frames, axis, YReal,
                    [.. scene.Series[s].Sessions.Select(x => x.StartMs).Order()]));

            var read = new List<PlotReading>(pts.Length);
            readings.Add(read);
            if (scene.BucketMs <= 0)
            {
                // Raw is one point per step, not per line: a frame's lines of one sign land as one
                // move, and the hover lists them.
                if (real.Length > 0)
                    read.Add(new PlotReading(axis.Map(real[0].Ms), YReal(real[0].Total), real[0].Ms, real[0].Ms,
                        real[0].Total, 0, real[0].Total, real[0].Total, 0));
                foreach (var (st, parts) in frames)
                    read.Add(new PlotReading(axis.Map(st.Ms), YReal(st.After), st.Ms, st.Ms, st.After, st.Change,
                        st.After, st.After, Math.Max(0, -st.Change), Math.Max(0, st.Change), parts.Length > 1 ? parts : null));
                lines.Add(Step(Decimate(read.Select(r => new PlotPoint(r.X, r.Y)).ToList()), right));
                foreach (var st in steps.Where(st => st.Change < 0))
                    raw.Add(new PlotLoss(s, axis.Map(st.Ms), YReal(st.Before), YReal(st.After), -st.Change, 0, false,
                        wipedAt.Contains(st.Ms)));
                continue;
            }

            var vertices = new List<PlotPoint>();
            long? previousClose = null;
            foreach (var b in Bucket(real, scene.BucketMs, zone))
            {
                double x0 = axis.Map(Math.Max(b.StartMs, scene.StartMs)), x1 = axis.Map(Math.Min(b.EndMs, scene.EndMs));
                var mid = (x0 + x1) / 2;
                bars.Add(new PlotBar(s, x0, x1, YReal(b.High), YReal(b.Low)));
                vertices.Add(new PlotPoint(mid, YReal(b.Close)));
                read.Add(new PlotReading(mid, YReal(b.Close), b.StartMs, b.EndMs, b.Close,
                    previousClose is { } p ? b.Close - p : 0, b.Low, b.High, b.Lost, b.Gained));
                previousClose = b.Close;
                if (b.Lost > 0)
                    raw.Add(new PlotLoss(s, mid, YReal(b.Low), YReal(b.Low), b.Lost, 0, false,
                        wipedAt.Any(ms => ms >= b.StartMs && ms < b.EndMs)));
            }
            lines.Add(Step(Decimate(vertices), right));
        }

        // One marker per slot of MarkerSlot width, the largest drop in it; markers any closer overlap
        // into a solid band and none of them stands out. A wipe's drop is always marked and labelled.
        var maxDrop = raw.Count == 0 ? 1 : raw.Max(l => l.Drop);
        var marked = raw.GroupBy(l => Math.Floor(l.X / MarkerSlot))
            .Select(g => g.MaxBy(l => l.Drop))
            .Concat(raw.Where(l => l.Permadeath))
            .ToHashSet();
        var labelled = marked.Where(l => l.Permadeath)
            .Concat(marked.Where(l => !l.Permadeath).OrderByDescending(l => l.Drop).Take(LabelledLosses))
            .ToHashSet();
        var losses = raw.Select(l => l with
        {
            Radius = marked.Contains(l)
                ? MinLossRadius + (MaxLossRadius - MinLossRadius) * Math.Sqrt((double)l.Drop / maxDrop)
                : 0,
            Labelled = labelled.Contains(l),
        }).ToList();

        var inWindow = sessions.Where(x => x.EndMs >= scene.StartMs && x.StartMs <= scene.EndMs).ToList();
        return new ScoreGraphPlot
        {
            Left = left, Top = top, Right = right, Bottom = bottom,
            GainMode = gainMode,
            Lines = lines,
            Readings = readings,
            Losses = losses,
            Moves = moves,
            Deaths = deaths,
            YTicks = ticks.Where(t => t >= yMin && t <= yMax).Select(t => (Y(t), t)).ToList(),
            XTicks = XTicksFor(scene.StartMs, scene.EndMs, axis, zone),
            Bars = bars,
            Marks = scene.Runs.Where(r => r.StartMs >= scene.StartMs && r.StartMs <= scene.EndMs)
                    .Select(r => new PlotMark(PlotMarkKind.Run, axis.Map(r.StartMs), axis.Map(r.EndMs), r, -1, string.Empty))
                .Concat(scene.Switches.Where(w => w.Session.StartMs >= scene.StartMs && w.Session.StartMs <= scene.EndMs)
                    .Select(w => new PlotMark(PlotMarkKind.Switch, axis.Map(w.Session.StartMs), axis.Map(w.Session.EndMs), w.Session, w.Series, w.From)))
                .OrderBy(m => m.X)
                .ToList(),
            Points = windows,
            SessionBands = inWindow.Select(x => (axis.Map(x.StartMs), axis.Map(x.EndMs))).ToList(),
            Gaps = axis.Gaps,
        };
    }

    /// <summary>Time-ordered readings as <see cref="ScoreStep"/>s: each change from the reading
    /// before, with consecutive changes of one sign and one stamp merged. Unchanged readings make no
    /// step.</summary>
    public static List<ScoreStep> Steps(IReadOnlyList<ScorePoint> points)
        => FrameSteps(points).Select(x => x.Step).ToList();

    /// <summary><see cref="Steps"/>, each with the individual changes merged into it, in order.</summary>
    public static List<(ScoreStep Step, long[] Parts)> FrameSteps(IReadOnlyList<ScorePoint> points)
    {
        var result = new List<(ScoreStep Step, List<long> Parts)>();
        for (var i = 1; i < points.Count; i++)
        {
            var change = points[i].Total - points[i - 1].Total;
            if (change == 0) continue;
            if (result.Count > 0 && result[^1].Step is var last
                && last.Ms == points[i].Ms && Math.Sign(last.Change) == Math.Sign(change) && last.After == points[i - 1].Total)
            {
                result[^1].Parts.Add(change);
                result[^1] = (last with { After = points[i].Total, Readings = last.Readings + 1 }, result[^1].Parts);
            }
            else
                result.Add((new ScoreStep(points[i].Ms, points[i - 1].Total, points[i].Total, 1), [change]));
        }
        return result.Select(x => (x.Step, x.Parts.ToArray())).ToList();
    }

    /// <summary>A climb never spans the start of a persona session: the steps either side of one
    /// were made in different logins, however close a zoomed-out or squeezed axis draws them.</summary>
    private static List<PlotMove> GroupMoves(int series, List<(ScoreStep Step, long[] Parts)> frames, ScoreTimeAxis axis,
        Func<long, double> y, long[] sessionStarts)
    {
        bool SessionStartsBetween(long afterMs, long uptoMs)
        {
            var at = Array.BinarySearch(sessionStarts, afterMs + 1);
            if (at < 0) at = ~at;
            return at < sessionStarts.Length && sessionStarts[at] <= uptoMs;
        }

        var result = new List<PlotMove>();
        var i = 0;
        while (i < frames.Count)
        {
            var sign = Math.Sign(frames[i].Step.Change);
            var j = i;
            while (j + 1 < frames.Count && Math.Sign(frames[j + 1].Step.Change) == sign
                   && axis.Map(frames[j + 1].Step.Ms) - axis.Map(frames[j].Step.Ms) <= MoveGap
                   && !SessionStartsBetween(frames[j].Step.Ms, frames[j + 1].Step.Ms))
                j++;
            var stairs = new List<PlotReading>(j - i + 1);
            for (var k = i; k <= j; k++)
            {
                var (st, parts) = frames[k];
                stairs.Add(new PlotReading(axis.Map(st.Ms), y(st.After), st.Ms, st.Ms, st.After, st.Change, st.After, st.After,
                    Math.Max(0, -st.Change), Math.Max(0, st.Change), parts.Length > 1 ? parts : null));
            }
            ScoreStep first = frames[i].Step, last = frames[j].Step;
            result.Add(new PlotMove(series, axis.Map(first.Ms), axis.Map(last.Ms), y(first.Before), y(last.After),
                first.Before, last.After, j - i + 1, first.Ms, last.Ms, stairs));
            i = j + 1;
        }
        return result;
    }

    /// <summary>Which of <paramref name="move"/>'s steps is nearest the point: the one the hover
    /// highlights and itemises.</summary>
    public static int StairNear(PlotMove move, double x, double y)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < move.Stairs.Count; i++)
        {
            var dx = move.Stairs[i].X - x;
            var dy = move.Stairs[i].Y - y;
            var d = dx * dx + dy * dy;
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    /// <summary>The climb or drop under the pointer: one whose extent, widened by
    /// <paramref name="reach"/>, holds the point. The nearest by X wins.</summary>
    public PlotMove? MoveAt(double x, double y, double reach)
    {
        PlotMove? best = null;
        var bestDistance = double.MaxValue;
        foreach (var m in Moves)
        {
            if (x < m.X0 - reach || x > m.X1 + reach) continue;
            if (y < Math.Min(m.YFrom, m.YTo) - reach || y > Math.Max(m.YFrom, m.YTo) + reach) continue;
            var d = x < m.X0 ? m.X0 - x : x > m.X1 ? x - m.X1 : 0;
            if (d < bestDistance)
            {
                best = m;
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>The reading nearest <paramref name="x"/> across every series, if one lies within
    /// <paramref name="reach"/>. Ties go to the earlier series.</summary>
    public (int Series, PlotReading Reading)? Nearest(double x, double reach)
    {
        (int, PlotReading)? best = null;
        var bestDistance = reach;
        for (var s = 0; s < Readings.Count; s++)
        {
            var r = Readings[s];
            if (r.Count == 0) continue;
            // First reading at or right of x; the nearest is it or the one before.
            int lo = 0, hi = r.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (r[mid].X < x) lo = mid + 1; else hi = mid;
            }
            foreach (var i in new[] { lo - 1, lo })
            {
                if (i < 0 || i >= r.Count) continue;
                var d = Math.Abs(r[i].X - x);
                if (d <= bestDistance && (best is null || d < bestDistance))
                {
                    best = (s, r[i]);
                    bestDistance = d;
                }
            }
        }
        return best;
    }

    /// <summary>The mark nearest <paramref name="x"/>, if one lies within <paramref name="reach"/>.
    /// A switch wins a tie with a run, being about a character rather than the client.</summary>
    public PlotMark? MarkNear(double x, double reach)
    {
        PlotMark? best = null;
        var bestDistance = double.MaxValue;
        foreach (var m in Marks)
        {
            var d = Math.Abs(m.X - x);
            if (d > reach) continue;
            if (d < bestDistance || (d == bestDistance && m.Kind == PlotMarkKind.Switch))
            {
                best = m;
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>How far series <paramref name="series"/>'s score moved across <paramref name="span"/>:
    /// its last reading in the span against the reading it held going in (or, with none before,
    /// its first reading in the span). Null when it has no reading inside the span.</summary>
    public long? NetChange(int series, TimeSpanMs span)
    {
        ScorePoint? before = null, first = null, last = null;
        foreach (var p in Points[series])
        {
            if (p.Ms < span.StartMs) { before = p; continue; }
            if (p.Ms > span.EndMs) break;
            first ??= p;
            last = p;
        }
        if (last is not { } l) return null;
        return l.Total - (before ?? first)!.Value.Total;
    }

    /// <summary>The reading series <paramref name="series"/> held at <paramref name="ms"/>: the last
    /// one at or before it, or null when it had none yet.</summary>
    public PlotReading? HeldAt(int series, long ms)
    {
        var r = Readings[series];
        int lo = 0, hi = r.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (r[mid].Ms <= ms) lo = mid + 1; else hi = mid;
        }
        return lo == 0 ? null : r[lo - 1];
    }

    /// <summary>The readings inside [start, end], preceded - when there is one - by the last reading
    /// before the window moved to <paramref name="startMs"/>, so the line starts at the left edge at
    /// the score the character actually had then.</summary>
    public static ScorePoint[] Window(ScorePoint[] points, long startMs, long endMs)
    {
        var result = new List<ScorePoint>();
        ScorePoint? seed = null;
        foreach (var p in points)
        {
            if (p.Ms < startMs) { seed = p; continue; }
            if (p.Ms > endMs) break;
            result.Add(p);
        }
        if (seed is { } s)
            result.Insert(0, s with { Ms = startMs });
        return [.. result];
    }

    /// <summary>Groups time-ordered readings into buckets of <paramref name="bucketMs"/>, aligned to
    /// <paramref name="zone"/>'s clock so a day bucket runs midnight to midnight.</summary>
    public static List<ScoreBucket> Bucket(IReadOnlyList<ScorePoint> points, long bucketMs, TimeZoneInfo zone)
    {
        var result = new List<ScoreBucket>();
        long? key = null;
        ScoreBucket current = default;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var offset = (long)zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(p.Ms)).TotalMilliseconds;
            var k = (long)Math.Floor((double)(p.Ms + offset) / bucketMs);
            var change = i > 0 ? p.Total - points[i - 1].Total : 0;
            long drop = Math.Max(0, -change), rise = Math.Max(0, change);
            if (k != key)
            {
                if (key is not null) result.Add(current);
                key = k;
                var start = k * bucketMs - offset;
                current = new ScoreBucket(start, start + bucketMs, p.Total, p.Total, p.Total, drop, rise);
                continue;
            }
            current = current with
            {
                Low = Math.Min(current.Low, p.Total),
                High = Math.Max(current.High, p.Total),
                Close = p.Total,
                Lost = current.Lost + drop,
                Gained = current.Gained + rise,
            };
        }
        if (key is not null) result.Add(current);
        return result;
    }

    /// <summary>The summary of windowed readings (see <see cref="Window"/>), or null when there are none.</summary>
    public static ScoreStats? Stats(IReadOnlyList<ScorePoint> points)
    {
        if (points.Count == 0) return null;
        long low = points[0].Total, high = low, gained = 0, lost = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var step = points[i].Total - points[i - 1].Total;
            if (step > 0) gained += step; else lost -= step;
            low = Math.Min(low, points[i].Total);
            high = Math.Max(high, points[i].Total);
        }
        return new ScoreStats(points[0].Total, low, high, gained, lost);
    }

    /// <summary>At most four vertices per whole-unit column of X: the first, lowest, highest and last
    /// in the order they occurred. The extremes of a column survive however many readings share it.</summary>
    public static List<PlotPoint> Decimate(IReadOnlyList<PlotPoint> points)
    {
        var result = new List<PlotPoint>(Math.Min(points.Count, 4096));
        var i = 0;
        while (i < points.Count)
        {
            var column = Math.Floor(points[i].X);
            int first = i, lowest = i, highest = i, last = i;
            for (i++; i < points.Count && Math.Floor(points[i].X) == column; i++)
            {
                // Screen Y grows downwards: the highest score is the smallest Y.
                if (points[i].Y > points[lowest].Y) lowest = i;
                if (points[i].Y < points[highest].Y) highest = i;
                last = i;
            }
            foreach (var k in new[] { first, lowest, highest, last }.Distinct().Order())
                result.Add(points[k]);
        }
        return result;
    }

    /// <summary>A step line through the points: the score holds until the next reading, then moves to
    /// it. The last reading holds to <paramref name="right"/>.</summary>
    public static List<PlotPoint> Step(IReadOnlyList<PlotPoint> points, double right)
    {
        var result = new List<PlotPoint>(points.Count * 2 + 1);
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
                result.Add(new PlotPoint(points[i].X, points[i - 1].Y));
            result.Add(points[i]);
        }
        if (points.Count > 0 && points[^1].X < right)
            result.Add(new PlotPoint(right, points[^1].Y));
        return result;
    }

    /// <summary>Round-number ticks covering [lo, hi], roughly <paramref name="count"/> of them.</summary>
    public static long[] NiceTicks(long lo, long hi, int count)
    {
        if (hi <= lo) hi = lo + 1;
        var rough = (double)(hi - lo) / Math.Max(1, count - 1);
        // Scores are whole numbers, so no step is finer than 1; that also keeps 10 * magnitude a
        // whole number no smaller than rough, so the search below always finds a step.
        var magnitude = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(rough))));
        // Whole-number steps only: 2.5 is a nice step for 25,000 and not for 2.5.
        var step = NiceSteps.Select(m => m * magnitude).Where(s => s == Math.Floor(s)).First(s => s >= rough);
        var stepL = Math.Max(1L, (long)step);
        var first = (long)Math.Floor((double)lo / stepL) * stepL;
        var ticks = new List<long>();
        for (var t = first; t < hi + stepL; t += stepL)
            ticks.Add(t);
        return [.. ticks];
    }

    private static List<(double X, string Label)> XTicksFor(long startMs, long endMs, ScoreTimeAxis axis, TimeZoneInfo zone)
    {
        // The finest step whose ticks, spread over the whole width, stay MinXTickSpacing apart. A
        // squeezed axis is uneven, so the spacing check below still drops any that crowd.
        var width = axis.Map(endMs) - axis.Map(startMs);
        var hours = (endMs - startMs) / 3_600_000d;
        var stepHours = HourSteps.FirstOrDefault(step => width / (hours / step) >= MinXTickSpacing, 24);
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(startMs), zone);
        var mark = new DateTimeOffset(start.Year, start.Month, start.Day, 0, 0, 0, start.Offset);
        var ticks = new List<(double, string)>();
        var lastX = double.NegativeInfinity;
        for (; mark.ToUnixTimeMilliseconds() <= endMs; mark = mark.AddHours(stepHours))
        {
            // Re-resolve the offset each step so a daylight-saving change keeps marks on local midnight.
            mark = new DateTimeOffset(mark.DateTime, zone.GetUtcOffset(mark.DateTime));
            var ms = mark.ToUnixTimeMilliseconds();
            if (ms < startMs) continue;
            var x = axis.Map(ms);
            if (x - lastX < MinXTickSpacing) continue;
            lastX = x;
            var label = stepHours < 24 && mark.Hour != 0
                ? mark.ToString("HH:mm", CultureInfo.InvariantCulture)
                : mark.ToString("ddd d", CultureInfo.InvariantCulture);
            ticks.Add((x, label));
        }
        return ticks;
    }
}

/// <summary>Maps time to X, linearly or with the gaps between active spans squeezed to a fixed width.
/// Monotone and continuous either way: a gap still spans its <see cref="ScoreGraphPlot.GapWidth"/>
/// linearly, so nothing that falls inside one is lost or reordered.</summary>
public sealed class ScoreTimeAxis
{
    private readonly (long StartMs, long EndMs, double X0, double X1)[] _pieces;

    private ScoreTimeAxis((long, long, double, double)[] pieces, IReadOnlyList<(double, double)> gaps)
    {
        _pieces = pieces;
        Gaps = gaps;
    }

    public IReadOnlyList<(double X0, double X1)> Gaps { get; }

    public static ScoreTimeAxis Linear(long startMs, long endMs, double x0, double width)
        => new([(startMs, Math.Max(endMs, startMs + 1), x0, x0 + width)], []);

    public static ScoreTimeAxis Squeezed(long startMs, long endMs, IEnumerable<TimeSpanMs> active,
        double x0, double width, double gapWidth)
    {
        var merged = new List<(long S, long E)>();
        foreach (var span in active
                     .Select(a => (S: Math.Max(a.StartMs, startMs), E: Math.Min(a.EndMs, endMs)))
                     .Where(a => a.E > a.S)
                     .OrderBy(a => a.S))
        {
            if (merged.Count > 0 && span.S <= merged[^1].E)
                merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, span.E));
            else
                merged.Add(span);
        }
        if (merged.Count == 0)
            return Linear(startMs, endMs, x0, width);

        var pieces = new List<(long S, long E, bool Gap)>();
        var cursor = startMs;
        foreach (var (s, e) in merged)
        {
            if (s > cursor) pieces.Add((cursor, s, true));
            pieces.Add((s, e, false));
            cursor = e;
        }
        if (endMs > cursor) pieces.Add((cursor, endMs, true));

        var gapCount = pieces.Count(p => p.Gap);
        var activeMs = merged.Sum(m => m.E - m.S);
        var activeWidth = Math.Max(width * 0.25, width - gapCount * gapWidth);
        var gapPx = gapCount == 0 ? 0 : (width - activeWidth) / gapCount;

        var result = new List<(long, long, double, double)>();
        var gaps = new List<(double, double)>();
        var x = x0;
        foreach (var (s, e, gap) in pieces)
        {
            var w = gap ? gapPx : activeWidth * (e - s) / activeMs;
            result.Add((s, e, x, x + w));
            if (gap) gaps.Add((x, x + w));
            x += w;
        }
        return new ScoreTimeAxis([.. result], gaps);
    }

    public double Map(long ms)
    {
        if (ms <= _pieces[0].StartMs) return _pieces[0].X0;
        if (ms >= _pieces[^1].EndMs) return _pieces[^1].X1;

        // The first piece whose end is at or after ms. Pieces are contiguous and in order.
        int lo = 0, hi = _pieces.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_pieces[mid].EndMs < ms) lo = mid + 1; else hi = mid;
        }
        var (s, e, x0, x1) = _pieces[lo];
        return e == s ? x0 : x0 + (x1 - x0) * (ms - s) / (e - s);
    }
}
