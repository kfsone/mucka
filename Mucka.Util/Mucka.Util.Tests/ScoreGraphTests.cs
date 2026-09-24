using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Commands;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>The $SCORE panel's MAUI-free half: the read, the plot arithmetic and the argument parse.
/// The panel and its drawing are in the MAUI assembly, which no suite reaches.</summary>
public sealed class ScoreGraphTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-score-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long Run(SqliteConnection c, string host, long started, long? ended)
        => Scalar(c, "INSERT INTO mucka_runs (started_ms, ended_ms, host) VALUES ($s,$e,$h); SELECT last_insert_rowid();",
            ("$s", started), ("$e", ended), ("$h", host));

    private static long Session(SqliteConnection c, long run, string? host, string persona, long started, long? ended)
        => Scalar(c, "INSERT INTO persona_sessions (mucka_run_id, host, persona, started_ms, ended_ms) VALUES ($r,$h,$p,$s,$e); SELECT last_insert_rowid();",
            ("$r", run), ("$h", host), ("$p", persona), ("$s", started), ("$e", ended));

    private static void Score(SqliteConnection c, long? session, long ts, long total)
        => Scalar(c, "INSERT INTO score_events (ts, encounter_open, total, persona_session_id) VALUES ($t,0,$v,$s); SELECT last_insert_rowid();",
            ("$t", ts), ("$v", total), ("$s", session));

    [Fact]
    public void Reader_keys_by_server_and_persona_and_falls_back_to_the_run_host()
    {
        using (var c = MuckaDb.Open(DbPath))
        {
            var uk = Run(c, "mud2.co.uk", 100, 900);
            var us = Run(c, "www.mud2.com", 100, 900);
            // NULL host, as on existing databases: the run's host stands in.
            var a = Session(c, uk, null, "Ollie", 100, 500);
            var b = Session(c, us, "www.mud2.com", "Ollie", 100, 500);
            Score(c, a, 200, 1000);
            Score(c, a, 300, 900);
            Score(c, b, 250, 50);
            Score(c, null, 260, 77);   // no persona session: cannot be placed on a character
            Score(c, a, 10, 5);        // before the window
        }

        var history = ScoreHistoryReader.Read(DbPath, sinceMs: 100, nowMs: 1000, currentRunId: -1);

        Assert.Equal([new ScorePoint(200, 1000), new ScorePoint(300, 900)],
            history.Points[new PersonaKey("MUD2.co.uk", "Ollie")]);
        Assert.Equal([new ScorePoint(250, 50)], history.Points[new PersonaKey("www.mud2.com", "Ollie")]);
        Assert.Equal(2, history.Points.Count);
        Assert.Equal(["mud2.co.uk", "www.mud2.com"], history.Hosts());
    }

    [Fact]
    public void Reader_bounds_open_sessions_by_now_for_this_run_and_by_the_last_reading_otherwise()
    {
        long current;
        using (var c = MuckaDb.Open(DbPath))
        {
            var crashed = Run(c, "h", 100, null);
            current = Run(c, "h", 600, null);
            var old = Session(c, crashed, "h", "A", 100, null);
            Score(c, old, 150, 1);
            Score(c, old, 180, 2);
            Session(c, current, "h", "A", 650, null);
        }

        var history = ScoreHistoryReader.Read(DbPath, 0, 1000, current);

        Assert.Equal([new TimeSpanMs(100, 180), new TimeSpanMs(650, 1000)], history.PersonaSessions[new PersonaKey("h", "A")]);
        Assert.Equal([("h", new TimeSpanMs(100, 100)), ("h", new TimeSpanMs(600, 1000))], history.Runs);
    }

    [Fact]
    public void Window_starts_at_the_score_held_when_it_opens()
    {
        ScorePoint[] points = [new(10, 100), new(20, 150), new(40, 90), new(60, 300)];

        Assert.Equal([new ScorePoint(30, 150), new ScorePoint(40, 90)], ScoreGraphPlot.Window(points, 30, 50));
        Assert.Equal(points[..1], ScoreGraphPlot.Window(points, 0, 15));
    }

    [Fact]
    public void Decimation_keeps_a_gain_and_a_loss_that_share_one_column()
    {
        // Six readings in column 5, screen Y (down is lower score): it rises to Y=20 and falls to
        // Y=110 before settling at 60. A mean or last-value bucket keeps only the 60.
        PlotPoint[] points = [new(5.1, 50), new(5.2, 40), new(5.3, 49), new(5.4, 20), new(5.5, 110), new(5.9, 60), new(7, 60)];

        var kept = ScoreGraphPlot.Decimate(points);

        Assert.Equal([new PlotPoint(5.1, 50), new PlotPoint(5.4, 20), new PlotPoint(5.5, 110), new PlotPoint(5.9, 60), new PlotPoint(7, 60)], kept);
    }

    [Fact]
    public void Every_negative_step_is_a_loss_and_the_largest_are_labelled()
    {
        var points = new List<ScorePoint> { new(0, 1000) };
        for (var i = 1; i <= 8; i++)
            points.Add(new(i * 10, points[^1].Total + (i % 2 == 0 ? -i * 10 : 500)));
        var scene = new ScoreGraphScene(0, 100, false, 0, [new ScoreGraphSeries("A", [.. points], [])], [], []);

        var plot = ScoreGraphPlot.Build(scene, 600, 300, TimeZoneInfo.Utc);

        Assert.Equal([20L, 40, 60, 80], plot.Losses.Select(l => l.Drop));
        Assert.All(plot.Losses, l => Assert.True(l.YAfter > l.YBefore, "a drop is drawn downwards"));
        Assert.Equal(4, plot.Losses.Count(l => l.Labelled));
        Assert.True(plot.Losses[^1].Radius > plot.Losses[0].Radius);
        Assert.False(plot.GainMode);
    }

    [Fact]
    public void Drops_closer_than_a_marker_slot_share_one_marker_and_keep_their_drop()
    {
        // Three drops a few units apart and one far away; 1000 ms over a 1000-unit plot is about 1:1.
        // Plot X = LeftMargin + ms, so 110..114 lands at 168..172, inside one 16-wide slot.
        ScorePoint[] points = [new(0, 1000), new(110, 900), new(112, 600), new(114, 550), new(900, 400)];
        var scene = new ScoreGraphScene(0, 1000, false, 0, [new ScoreGraphSeries("A", points, [])], [], []);

        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);

        Assert.Equal(4, plot.Losses.Count);
        Assert.Equal([300L, 150], plot.Losses.Where(l => l.Radius > 0).Select(l => l.Drop));
    }

    [Fact]
    public void Buckets_align_to_local_time_and_sum_every_drop_in_them()
    {
        const long Hour = 3_600_000;
        // A whole-hour offset puts local and UTC hour boundaries on the same instants; a half-hour
        // zone is what shows the offset is applied.
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-half", TimeSpan.FromMinutes(30), "plus-half", "plus-half");
        ScorePoint[] points =
        [
            new(0, 1000),                 // local 00:30 -> bucket [00:00, 01:00) local
            new(Hour / 4, 1300),          // local 00:45
            new(Hour / 3, 800),           // local 00:50, drop 500
            new(Hour / 2, 700),           // local 01:00 -> next bucket, drop 100 from the previous close
            new(Hour, 900),
        ];

        var buckets = ScoreGraphPlot.Bucket(points, Hour, zone);

        Assert.Equal(
        [
            new ScoreBucket(-Hour / 2, Hour / 2, 800, 1300, 800, 500, 300),
            new ScoreBucket(Hour / 2, 3 * Hour / 2, 700, 900, 900, 100, 200),
        ], buckets);
    }

    [Fact]
    public void A_bucketed_scale_draws_a_bar_per_bucket_and_one_loss_per_bucket()
    {
        const long Minute = 60_000;
        ScorePoint[] points = [new(0, 1000), new(10_000, 1500), new(20_000, 900), new(30_000, 950), new(70_000, 940)];
        var scene = new ScoreGraphScene(0, 2 * Minute, false, Minute, [new ScoreGraphSeries("A", points, [])], [], []);

        var plot = ScoreGraphPlot.Build(scene, 600, 300, TimeZoneInfo.Utc);

        Assert.Equal(2, plot.Bars.Count);
        Assert.True(plot.Bars[0].YLow - plot.Bars[0].YHigh > plot.Bars[1].YLow - plot.Bars[1].YHigh,
            "the bucket that rose and fell is the taller bar");
        Assert.Equal([600L, 10], plot.Losses.Select(l => l.Drop));
    }

    [Fact]
    public void Hover_finds_the_nearest_reading_and_reports_the_score_as_printed()
    {
        // Two characters: the axis is gain-since-open, but the readout is the real score.
        var scene = new ScoreGraphScene(0, 1000, false, 0,
        [
            new ScoreGraphSeries("A", [new(0, 5000), new(400, 5300), new(600, 5100)], []),
            new ScoreGraphSeries("B", [new(0, 100), new(500, 180)], []),
        ], [], []);
        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);
        var x = ScoreGraphPlot.LeftMargin;

        var hit = plot.Nearest(x + 590, reach: 20);

        Assert.NotNull(hit);
        Assert.Equal(0, hit.Value.Series);
        Assert.Equal(new PlotReading(x + 600, hit.Value.Reading.Y, 600, 600, 5100, -200, 5100, 5100, 200), hit.Value.Reading);
        Assert.Null(plot.Nearest(x + 250, reach: 20));
        Assert.Equal(180, plot.HeldAt(1, 600)!.Value.Total);
        Assert.Equal(100, plot.HeldAt(1, 499)!.Value.Total);
    }

    [Fact]
    public void Steps_merge_one_stamp_and_one_sign_and_keep_a_gain_and_a_loss_apart()
    {
        // Six swamp drops in one frame share a stamp (observed); then a gain and a loss in one frame.
        ScorePoint[] points =
        [
            new(0, 100),
            new(10, 130), new(10, 152), new(10, 174),
            new(11, 180),                               // a frame straddling a millisecond: its own step
            new(20, 2180), new(20, 1180),               // won 2,000 and lost 1,000 in one frame
            new(30, 1180),                              // no change: no step
        ];

        Assert.Equal(
        [
            new ScoreStep(10, 100, 174, 3),
            new ScoreStep(11, 174, 180, 1),
            new ScoreStep(20, 180, 2180, 1),
            new ScoreStep(20, 2180, 1180, 1),
        ], ScoreGraphPlot.Steps(points));
    }

    [Fact]
    public void Raw_is_one_reading_per_step_with_a_frames_lines_as_its_parts()
    {
        ScorePoint[] points = [new(0, 100), new(10, 130), new(10, 152), new(10, 174), new(20, 170)];
        var scene = new ScoreGraphScene(0, 100, false, 0, [new ScoreGraphSeries("A", points, [])], [], []);

        var plot = ScoreGraphPlot.Build(scene, 600, 300, TimeZoneInfo.Utc);

        var read = plot.Readings[0];
        Assert.Equal([100L, 174, 170], read.Select(r => r.Total));
        Assert.Equal([30L, 22, 22], read[1].Parts!);
        Assert.Null(read[2].Parts);
        Assert.Equal(74, read[1].Change);
    }

    [Fact]
    public void A_climb_is_steps_of_one_sign_that_sit_together_on_the_axis()
    {
        // 1 ms per plot unit: steps at 100..110 sit within MoveGap of each other, 500 does not.
        ScorePoint[] points = [new(0, 1000), new(100, 1100), new(104, 1150), new(110, 1300), new(112, 1250), new(500, 1400)];
        var scene = new ScoreGraphScene(0, 1000, false, 0, [new ScoreGraphSeries("A", points, [])], [], []);
        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);

        Assert.Equal([300L, -50, 150], plot.Moves.Select(m => m.Change));
        Assert.Equal([3, 1, 1], plot.Moves.Select(m => m.Steps));
        Assert.Equal(3, plot.Moves[0].Stairs.Count);

        var climb = plot.Moves[0];
        Assert.Equal(climb, plot.MoveAt((climb.X0 + climb.X1) / 2, (climb.YFrom + climb.YTo) / 2, reach: 5));
        Assert.Null(plot.MoveAt(ScoreGraphPlot.LeftMargin + 300, climb.YTo, reach: 5));
    }

    [Fact]
    public void A_climb_breaks_where_a_persona_session_starts()
    {
        // Two gains 2 ms apart - one riser on screen - but a login began between them.
        ScorePoint[] points = [new(0, 1000), new(100, 1100), new(102, 1200)];
        TimeSpanMs[] sessions = [new(0, 101), new(101, 500)];
        var scene = new ScoreGraphScene(0, 1000, false, 0, [new ScoreGraphSeries("A", points, sessions)], [], []);
        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);

        Assert.Equal([100L, 100], plot.Moves.Select(m => m.Change));
    }

    [Fact]
    public void A_wipe_is_marked_where_the_line_stood_and_its_drops_are_ordinary()
    {
        // A small drop in the wiped login's last frame, the wipe at 200, and the restart at 200 points
        // in the next login: two drops, both plain, and one death at the wipe.
        ScorePoint[] points = [new(0, 8000), new(100, 8243), new(150, 8218), new(300, 200), new(400, 260)];
        var scene = new ScoreGraphScene(0, 1000, false, 0, [new ScoreGraphSeries("A", points, [], [200])], [], []);
        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);

        Assert.Equal([25L, 8018], plot.Losses.Select(l => l.Drop));
        var death = Assert.Single(plot.Deaths);
        Assert.Equal(ScoreGraphPlot.LeftMargin + 200, death.X, 6);
        Assert.Equal(plot.Losses[1].YBefore, death.Y, 6);
    }

    [Fact]
    public void Zero_runs_the_axis_down_to_zero()
    {
        var series = new ScoreGraphSeries("A", [new(0, 104_000), new(50, 114_000)], []);

        var fitted = ScoreGraphPlot.Build(new ScoreGraphScene(0, 100, false, 0, [series], [], []), 600, 300, TimeZoneInfo.Utc);
        var zero = ScoreGraphPlot.Build(new ScoreGraphScene(0, 100, false, 0, [series], [], [], Zero: true), 600, 300, TimeZoneInfo.Utc);

        Assert.DoesNotContain(fitted.YTicks, t => t.Value == 0);
        Assert.Contains(zero.YTicks, t => t.Value == 0 && Math.Abs(t.Y - zero.Bottom) < 1e-6);
    }

    [Fact]
    public void Zero_plots_several_characters_by_score_not_by_gain()
    {
        // One gained, one lost: as gains, the loser would sit below a zero line.
        ScoreGraphSeries[] series =
        [
            new("Big", [new(0, 100_000), new(50, 110_000)], []),
            new("Small", [new(0, 8_000), new(50, 200)], []),
        ];

        var plot = ScoreGraphPlot.Build(new ScoreGraphScene(0, 100, false, 0, series, [], [], Zero: true), 600, 300, TimeZoneInfo.Utc);

        Assert.False(plot.GainMode);
        Assert.All(plot.Lines.SelectMany(l => l), p => Assert.True(p.Y <= plot.Bottom + 1e-6, "nothing below the zero line"));
        Assert.Equal(0, plot.YTicks.Min(t => t.Value));
    }

    [Fact]
    public void Reader_reports_a_wipe_at_the_end_of_its_persona_session()
    {
        using (var c = MuckaDb.Open(DbPath))
        {
            var run = Run(c, "h", 0, 900);
            var wiped = Session(c, run, "h", "A", 100, 500);
            Scalar(c, "UPDATE persona_sessions SET ended_note = 'permadeath' WHERE id = $id; SELECT 0;", ("$id", wiped));
            var died = Session(c, run, "h", "A", 600, 700);
            Scalar(c, "UPDATE persona_sessions SET ended_note = 'died' WHERE id = $id; SELECT 0;", ("$id", died));
        }

        var history = ScoreHistoryReader.Read(DbPath, 0, 1000, -1);

        Assert.Equal([500L], history.Permadeaths[new PersonaKey("h", "A")]);
    }

    [Fact]
    public void Stats_count_gains_and_losses_separately()
    {
        ScorePoint[] points = [new(0, 1000), new(1, 1500), new(2, 900), new(3, 950)];

        Assert.Equal(new ScoreStats(1000, 900, 1500, 550, 600), ScoreGraphPlot.Stats(points));
        Assert.Null(ScoreGraphPlot.Stats([]));
    }

    [Fact]
    public void A_switch_is_a_selected_session_after_a_different_character()
    {
        var a = new PersonaKey("h", "A");
        var b = new PersonaKey("h", "B");
        var elsewhere = new PersonaKey("other", "C");
        var history = new ScoreHistory(1000, new Dictionary<PersonaKey, ScorePoint[]>(),
            new Dictionary<PersonaKey, TimeSpanMs[]>
            {
                [a] = [new(10, 20), new(30, 40), new(70, 80)],
                [b] = [new(50, 60)],
                [elsewhere] = [new(65, 66)],
            }, []);

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h" };

        // A, A (no switch), B (switch, but B is not selected), A (switch).
        Assert.Equal([(a, new TimeSpanMs(70, 80), b)], history.Switches(hosts, new HashSet<PersonaKey> { a }));
        Assert.Equal([(b, new TimeSpanMs(50, 60), a), (a, new TimeSpanMs(70, 80), b)], history.Switches(hosts, new HashSet<PersonaKey> { a, b }));
    }

    [Fact]
    public void A_marks_score_effect_is_its_last_reading_against_the_score_held_going_in()
    {
        ScorePoint[] points = [new(0, 1000), new(100, 1200), new(250, 900), new(300, 950), new(500, 2000)];
        var scene = new ScoreGraphScene(0, 1000, false, 0, [new ScoreGraphSeries("A", points, [])],
            [new TimeSpanMs(200, 400)], [new ScoreSwitch(0, new TimeSpanMs(600, 700), "B")]);
        var plot = ScoreGraphPlot.Build(scene, 1000 + ScoreGraphPlot.LeftMargin + ScoreGraphPlot.RightMargin, 300, TimeZoneInfo.Utc);

        // Held 1,200 going in; last reading inside is 950.
        Assert.Equal(-250, plot.NetChange(0, new TimeSpanMs(200, 400)));
        // Nothing before: measured from the first reading inside.
        Assert.Equal(200, plot.NetChange(0, new TimeSpanMs(0, 150)));
        Assert.Null(plot.NetChange(0, new TimeSpanMs(600, 700)));

        var x = ScoreGraphPlot.LeftMargin;
        Assert.Equal([PlotMarkKind.Run, PlotMarkKind.Switch], plot.Marks.Select(m => m.Kind));
        Assert.Equal(PlotMarkKind.Switch, plot.MarkNear(x + 603, reach: 6)!.Value.Kind);
        Assert.Equal(new TimeSpanMs(200, 400), plot.MarkNear(x + 198, reach: 6)!.Value.Span);
        Assert.Null(plot.MarkNear(x + 400, reach: 6));
    }

    [Fact]
    public void Several_characters_plot_gain_since_the_window_opened()
    {
        var scene = new ScoreGraphScene(0, 100, false, 0,
        [
            new ScoreGraphSeries("Big", [new(0, 100_000), new(50, 100_500)], []),
            new ScoreGraphSeries("Small", [new(0, 200), new(50, 700)], []),
        ], [], []);

        var plot = ScoreGraphPlot.Build(scene, 600, 300, TimeZoneInfo.Utc);

        Assert.True(plot.GainMode);
        // Both gained 500, so both lines end at the same height.
        Assert.Equal(plot.Lines[0][^1].Y, plot.Lines[1][^1].Y);
    }

    [Fact]
    public void Squeeze_collapses_time_outside_sessions_to_a_fixed_gap()
    {
        TimeSpanMs[] sessions = [new(100, 200), new(800, 900)];

        var axis = ScoreTimeAxis.Squeezed(0, 1000, sessions, 0, 216, 8);

        // Three gaps (before, between, after) of 8 each; the two sessions share the other 192.
        Assert.Equal([(0d, 8d), (104d, 112d), (208d, 216d)], axis.Gaps);
        Assert.Equal(8, axis.Map(100));
        Assert.Equal(56, axis.Map(150));
        Assert.Equal(112, axis.Map(800));
        Assert.Equal(216, axis.Map(1000));
    }

    [Fact]
    public void Squeeze_with_no_sessions_is_linear()
    {
        var axis = ScoreTimeAxis.Squeezed(0, 1000, [], 0, 100, 8);

        Assert.Empty(axis.Gaps);
        Assert.Equal(50, axis.Map(500));
    }

    [Fact]
    public void Nice_ticks_cover_the_range_in_round_steps()
    {
        Assert.Equal([104_000L, 106_000, 108_000, 110_000, 112_000, 114_000], ScoreGraphPlot.NiceTicks(104_278, 113_654, 6));
        Assert.Equal([0L, 1], ScoreGraphPlot.NiceTicks(0, 0, 5));
        // A one-point range asked for many ticks - a flat day on a tall panel - steps by 1.
        Assert.Equal([1000L, 1001], ScoreGraphPlot.NiceTicks(1000, 1001, 17));
    }

    [Theory]
    [InlineData("", 3, null)]
    [InlineData("7", 7, null)]
    [InlineData("Kayfez", 3, "Kayfez")]
    [InlineData("Kayfez 30", 30, "Kayfez")]
    [InlineData("  30   Kayfez ", 30, "Kayfez")]
    [InlineData("0", 1, null)]
    [InlineData("500", 90, null)]
    [InlineData("99999999999999", 90, null)]
    public void Score_args_tell_days_from_persona_by_shape(string text, int days, string? persona)
    {
        var parsed = ScoreCommandArgs.Parse(text, out var error);

        Assert.Null(error);
        Assert.Equal(new ScoreCommandArgs(days, persona), parsed);
    }

    [Theory]
    [InlineData("3 4")]
    [InlineData("Ollie Kayfez")]
    [InlineData("3d")]
    public void Score_args_reject_what_they_cannot_place(string text)
    {
        Assert.Null(ScoreCommandArgs.Parse(text, out var error));
        Assert.NotNull(error);
    }
}
