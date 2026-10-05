using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// The login banner's mail verdict. Fixtures are the verbatim tails captured in the wire log
/// (mud2.co.uk; both verdicts, and no other wording).
/// </summary>
public class MailNoticeTests
{
    [Fact]
    public void OneItem_IsReadFromTheCapturedBannerTail()
    {
        var n = ShellText.NormalizeWhitespace(
            "[Checking mail...]\r\n[You have mail: 1 item]\r\nMUD login menu.\r\nOption (H for help): ");
        Assert.Equal(1, ShellText.MailItemsWaiting(n));
    }

    [Fact]
    public void NoMail_IsZero()
    {
        var n = ShellText.NormalizeWhitespace(
            "[Checking mail...]\r\n[You have no mail]\r\nMUD login menu.\r\nOption (H for help): ");
        Assert.Equal(0, ShellText.MailItemsWaiting(n));
    }

    [Fact]
    public void PluralIsMatchedOnTheSameShape()
    {
        Assert.Equal(3, ShellText.MailItemsWaiting("[You have mail: 3 items]"));
    }

    [Theory]
    [InlineData("[You have mail: 99999999999 items]")]
    [InlineData("[You have mail: \u0661 item]")]
    public void AbsurdOrNonAsciiCounts_DoNotThrow(string text)
        => Assert.Equal(0, ShellText.MailItemsWaiting(text));

    [Fact]
    public void AMailLineWithNoCount_StillMeansSomethingIsWaiting()
    {
        Assert.Equal(1, ShellText.MailItemsWaiting("[You have mail]"));
    }

    /// <summary>The verdict can arrive in a later socket read than "[Checking mail...]", so a
    /// buffer that has the one and not yet the other must read as no verdict, not as a failure.</summary>
    [Fact]
    public void BannerWithoutAVerdictYet_IsZero()
    {
        Assert.Equal(0, ShellText.MailItemsWaiting("[Checking mail...]"));
    }

    /// <summary>The wording that appears once a player is inside the mail program must not read as
    /// a login verdict.</summary>
    [Theory]
    [InlineData("[H for help, end-of-line to read your mail]\r\nMAIL>")]
    [InlineData("M - persona-to-persona mail                  O - as C")]
    [InlineData("\"Might be easier for him to message me there than trying to use mail here, lol")]
    public void MailProgramAndChatterAreNotAVerdict(string text)
        => Assert.Equal(0, ShellText.MailItemsWaiting(ShellText.NormalizeWhitespace(text)));

    [Fact]
    public void Silence_HoldsForTheDayAndForNoMoreItemsThanWereIgnored()
    {
        var day = new DateOnly(2026, 10, 4);
        var silence = new MailSilence(day, 1);

        Assert.True(silence.Silences(1, day));
        Assert.False(silence.Silences(2, day));                 // new mail re-alerts
        Assert.False(silence.Silences(1, day.AddDays(1)));      // tomorrow re-alerts
    }

    [Fact]
    public void Silence_RoundTripsThroughItsIniForm()
    {
        var silence = new MailSilence(new DateOnly(2026, 10, 4), 2);
        Assert.Equal("2026-10-04:2", silence.Serialize());
        Assert.Equal(silence, MailSilence.TryParse(silence.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("2026-10-04")]
    [InlineData("2026-10-04:x")]
    [InlineData("2026-13-40:1")]
    [InlineData("2026-10-04:-1")]
    public void Silence_ParseOfGarbageIsNotSilenced(string? text)
        => Assert.Null(MailSilence.TryParse(text));
}
