using System.Text;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Full-sheet parse of MUD2's `sc` (score) output — the ONLY source for carried weight, objects
/// carried, persona value and sex (the FES heartbeat carries none of them).
///
/// The three sheets below are real captures from three consecutive `sc` commands in one session,
/// verbatim. They are the regression: between them they cover an absent effective clause (= equal
/// to base), an effective value ABOVE base (a buff), "weight carried: nothing" (zero, which is a
/// measurement), a bare status word on its own line ("glowing"), and a sheet with no "current site"
/// line at all.
///
/// Asserted through MudSession rather than the analyzer directly, because the sheet arrives one
/// line at a time and it is MudSession.MergeStats that folds those per-line snapshots into the
/// character sheet the rest of the client reads.
/// </summary>
public class ScoreSheetTests : IDisposable
{
    // C02+C01 game-mode prompt variant — the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    // ── The three captures, verbatim ────────────────────────────────────────────
    private const string Sheet1 = """
        *sc
        name:           Ollie
        sex:            male
        strength:       100
        dexterity:      100     effective dexterity:    98
        stamina:        110     max:    110
        magic:          110
        score:  51,574 points   this game:      10 points       value:  10,389 points
        level:  9       warlock
        weight carried: 75g     max:    100kg
        objects carried:        2       max:    12
        games played:   93
        current site: the place known as "dense forest".
        No. of Tasks completed: 6 - #1 #2 #3 #4 #5 #6
        Time left until survival bonus: 27m 22s
        """;

    private const string Sheet2 = """
        *sc
        name:           Ollie
        sex:            male
        strength:       100     effective strength:     105
        dexterity:      100
        stamina:        110     max:    110
        magic:          90
        score:  51,939 points   this game:      375 points      value:  10,462 points
        level:  9       warlock
        weight carried: nothing max:    100kg
        objects carried:        0       max:    12
        games played:   93
        glowing
        current site: the place known as "dense forest".
        No. of Tasks completed: 6 - #1 #2 #3 #4 #5 #6
        Time left until survival bonus: 20m 14s
        """;

    private const string Sheet3 = """
        *sc
        name:           Ollie
        sex:            male
        strength:       100
        dexterity:      100
        stamina:        83      max:    110
        magic:          70
        score:  52,241 points   this game:      0 points        value:  10,523 points
        level:  9       warlock
        weight carried: nothing max:    100kg
        objects carried:        0       max:    12
        games played:   94
        No. of Tasks completed: 6 - #1 #2 #3 #4 #5 #6
        Time left until survival bonus: 29m 44s
        """;

    private readonly MudSession _session;

    public ScoreSheetTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
            ScoreRefreshInterval = TimeSpan.FromSeconds(60),   // and the sheet refresh with it
        });
    }

    public void Dispose() => _session.Dispose();

    /// <summary>Feed a sheet as the server sends it (CRLF-terminated lines) and return the merged
    /// character sheet the client now holds.</summary>
    private GameStatsSnapshot Parse(string sheet)
    {
        _session.Feed(GameModeEntry);
        var wire = sheet.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
        _session.Feed(Encoding.Latin1.GetBytes(wire));
        return _session.CurrentStats;
    }

    [Fact]
    public void Sheet1_ParsesEveryField()
    {
        var s = Parse(Sheet1);

        Assert.Equal("male", s.Sex);
        // No "effective strength:" clause — the server prints it only when it DIFFERS from the
        // base, so absence means equal, not unknown.
        Assert.Equal(100, s.RawStrength);
        Assert.Equal(100, s.Strength);
        Assert.Equal(100, s.RawDexterity);
        Assert.Equal(98,  s.Dexterity);
        Assert.Equal(110, s.Stamina);
        Assert.Equal(110, s.MaxStamina);
        Assert.Equal(110, s.CurrentMagic);
        Assert.Equal(51_574, s.Score);
        Assert.Equal(10,     s.ScoreThisGame);
        Assert.Equal(10_389, s.PlayerValue);
        Assert.Equal(9, s.Level);
        Assert.Equal(2,  s.ObjectsCarried);
        Assert.Equal(12, s.MaxObjectsCarried);
        Assert.Equal(93, s.GamesPlayed);
    }

    [Fact]
    public void Sheet2_ParsesEveryField_EffectiveAboveBase_AndNothingCarried()
    {
        var s = Parse(Sheet2);

        Assert.Equal("male", s.Sex);
        // A buff: effective is HIGHER than base. Nothing clamps it to the base value.
        Assert.Equal(100, s.RawStrength);
        Assert.Equal(105, s.Strength);
        // No effective dexterity clause this time — equal to base.
        Assert.Equal(100, s.RawDexterity);
        Assert.Equal(100, s.Dexterity);
        Assert.Equal(110, s.Stamina);
        Assert.Equal(110, s.MaxStamina);
        Assert.Equal(90,  s.CurrentMagic);
        Assert.Equal(51_939, s.Score);
        Assert.Equal(375,    s.ScoreThisGame);
        Assert.Equal(10_462, s.PlayerValue);
        Assert.Equal(9, s.Level);
        // "nothing" is the server's word for an empty pack: ZERO, not "not reported".
        Assert.Equal(0,  s.ObjectsCarried);
        Assert.Equal(12, s.MaxObjectsCarried);
        Assert.Equal(93, s.GamesPlayed);
    }

    [Fact]
    public void Sheet3_ParsesEveryField_NoCurrentSiteLine_ZeroThisGame()
    {
        var s = Parse(Sheet3);

        Assert.Equal("male", s.Sex);
        Assert.Equal(100, s.RawStrength);
        Assert.Equal(100, s.Strength);
        Assert.Equal(100, s.RawDexterity);
        Assert.Equal(100, s.Dexterity);
        Assert.Equal(83,  s.Stamina);
        Assert.Equal(110, s.MaxStamina);
        Assert.Equal(70,  s.CurrentMagic);
        Assert.Equal(52_241, s.Score);
        Assert.Equal(0,      s.ScoreThisGame);   // zero is a reading, not an absence
        Assert.Equal(10_523, s.PlayerValue);
        Assert.Equal(9, s.Level);
        Assert.Equal(0,  s.ObjectsCarried);
        Assert.Equal(12, s.MaxObjectsCarried);
        Assert.Equal(94, s.GamesPlayed);
    }

    [Fact]
    public void GlowingLine_DoesNotDisturbTheParse()
    {
        // Sheet 2 carries a bare status word on its own line. It must neither break the sheet nor
        // be mistaken for a stat: the lines on either side of it still parse.
        var s = Parse(Sheet2);
        Assert.Equal(9,  s.Level);            // the line before "glowing" (via level:)
        Assert.Equal(93, s.GamesPlayed);      // the line before it
        Assert.Equal(0,  s.ObjectsCarried);
    }

    [Fact]
    public void ConsecutiveSheets_LaterReadingsReplaceEarlier()
    {
        // The three captures are consecutive: feeding them in order must leave the client holding
        // the LAST one, including where a value went down (magic 110 → 70) or a carried pack
        // emptied (75g → nothing).
        Parse(Sheet1);
        Parse(Sheet2);
        var s = Parse(Sheet3);

        Assert.Equal(70, s.CurrentMagic);
        Assert.Equal(0,  s.ObjectsCarried);
        Assert.Equal(94, s.GamesPlayed);
        Assert.Equal(52_241, s.Score);
        // The buff in sheet 2 is gone in sheet 3, whose strength line has no effective clause —
        // absence means "back to base", so the stale 105 must not survive.
        Assert.Equal(100, s.Strength);
    }

    [Fact]
    public void TabSeparatedSheet_ParsesTheSameWay()
    {
        // The live sheet aligns its columns with tabs; the pasted capture shows them as runs of
        // spaces. Both are whitespace to the parser and must give the same answer.
        var s = Parse(
            "name:\tOllie\n" +
            "sex:\tfemale\n" +
            "strength:\t94\teffective strength:\t47\n" +
            "weight carried:\t2kg\tmax:\t100kg\n" +
            "objects carried:\t3\tmax:\t12\n" +
            "score:\t1,785 points\tthis game:\t-40 points\tvalue:\t357 points\n");

        Assert.Equal("female", s.Sex);
        Assert.Equal(94, s.RawStrength);
        Assert.Equal(47, s.Strength);
        Assert.Equal(3, s.ObjectsCarried);
        Assert.Equal(1785, s.Score);
        Assert.Equal(-40, s.ScoreThisGame);   // a bad game can cost points
        Assert.Equal(357, s.PlayerValue);
    }

    [Fact]
    public void WrappedScoreLine_StillYieldsAllThreeFigures()
    {
        // At narrow widths the ~70-column score line wraps and the tail arrives on its own line
        // (see tools/combat/TEXT-WRAPPING-REVIEW.md). Each figure is matched independently, so the
        // wrap costs nothing.
        var s = Parse(
            "score:  47,297 points   this game:      0\n" +
            "points        value:  9,534 points\n");

        Assert.Equal(47_297, s.Score);
        Assert.Equal(0, s.ScoreThisGame);
        Assert.Equal(9_534, s.PlayerValue);
    }

    [Fact]
    public void ValueProbeReply_IsNotMistakenForOurOwnValue()
    {
        // The `value <name>` sniff reply names a point value too. It has no "value:" token, so it
        // must never be read as our own persona value.
        var s = Parse("The value of Polly the witch is 4,120 points.\n");
        Assert.Null(s.PlayerValue);
    }
}
