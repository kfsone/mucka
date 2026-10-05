using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// The login banner's mail verdict, "[You have mail: N item]". The arrival notice is
/// <c>MailNotice</c>, tested in mudsharp.Tests. Fixtures are the verbatim tails captured in the wire
/// log (mud2.co.uk; both verdicts, and no other wording).
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
}
