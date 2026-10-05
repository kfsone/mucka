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

    [Theory]
    [InlineData("+- You have new mail from Drizzle -+", "Drizzle")]
    [InlineData("+- You have new mail from Kram -+", "Kram")]
    public void NewMailNotice_IsReadFromTheCapturedLine(string line, string from)
    {
        Assert.True(ShellText.TryParseNewMailLine(ShellText.NormalizeWhitespace(line), out var sender));
        Assert.Equal(from, sender);
    }

    /// <summary>The notice repeated through say, as captured: a speaker wraps it, so it is not mail.</summary>
    [Theory]
    [InlineData("Kayfez the pioneer says \"+- You have new mail from Drizzle -+\".")]
    [InlineData("\"+- You have new mail from Drizzle -+")]
    [InlineData("[Message number 14201 sent to Ollie]")]
    [InlineData("[You have mail: 1 item]")]
    public void NewMailNotice_RejectsEverythingButTheBareLine(string line)
        => Assert.False(ShellText.TryParseNewMailLine(ShellText.NormalizeWhitespace(line), out _));

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
