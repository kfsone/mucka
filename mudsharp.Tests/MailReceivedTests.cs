using System.Text;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests;

/// <summary>
/// <see cref="MudSession.MailReceived"/>, fed the bytes the wire log holds (run rows
/// "\r\n+- You have new mail from Drizzle -+\r\n" and the same text wrapped by a speaker).
/// Before the game starts the connection's read loop emits the buffered partial line after every
/// read, so <see cref="Feed"/> does the same: that is the path a notice split by a read takes.
/// </summary>
public class MailReceivedTests
{
    private static List<string> Feed(params string[] reads)
    {
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),
        });
        session.SetupInjectEnabled = false;
        var senders = new List<string>();
        session.MailReceived += senders.Add;
        foreach (var read in reads)
        {
            session.Feed(Encoding.Latin1.GetBytes(read));
            session.EmitPartial();
        }
        return senders;
    }

    [Fact]
    public void TheCapturedNotice_RaisesOnceWithTheSender()
        => Assert.Equal(new[] { "Drizzle" }, Feed("\r\n+- You have new mail from Drizzle -+\r\n"));

    [Fact]
    public void ANoticeSplitMidText_RaisesOnce()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("\r\n+- You have new mail from Dri", "zzle -+\r\n"));

    [Fact]
    public void ANoticeSplitInsideItsOwnPrefix_RaisesOnce()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("\r\n+", "- You have new mail from Drizzle -+\r\n"));

    /// <summary>The read ends exactly between "-+" and the newline: the partial is the whole notice
    /// and the line that follows is empty.</summary>
    [Fact]
    public void ANoticeWhoseReadEndsBeforeItsNewline_RaisesOnce()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("\r\n+- You have new mail from Drizzle -+", "\r\n"));

    [Fact]
    public void ANoticeAfterAPromptPartial_RaisesOnce()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("MAIL>", "\r\n+- You have new mail from Drizzle -+\r\n"));

    [Fact]
    public void TwoNotices_RaiseTwice()
        => Assert.Equal(new[] { "Kram", "Drizzle" },
            Feed("\r\n+- You have new mail from Kram -+\r\n+- You have new mail from Dri", "zzle -+\r\n"));

    /// <summary>A held head that never turns into a notice must not leak into the next line.</summary>
    [Fact]
    public void AHeadThatNeverCompletes_DoesNotPoisonTheNextNotice()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("+- some other text", "\r\nThe door is closed.\r\n+- You have new mail from Drizzle -+\r\n"));

    /// <summary>Past the cap the head is dropped rather than grown without bound; a notice that
    /// follows still arrives.</summary>
    [Fact]
    public void AnOversizedHead_IsDroppedAndTheNextNoticeStillArrives()
        => Assert.Equal(new[] { "Drizzle" },
            Feed("+" + new string('x', 300), "\r\n+- You have new mail from Drizzle -+\r\n"));

    [Fact]
    public void TheNoticeRepeatedThroughSay_IsNotMail()
        => Assert.Empty(Feed("Kayfez the pioneer says \"+- You have new mail from Drizzle -+\".\r\n"));

    [Fact]
    public void OrdinaryLines_RaiseNothing()
        => Assert.Empty(Feed("You have no mail to read.\r\nThe door is closed.\r\n"));

    [Theory]
    [InlineData("+- You have new mail from Drizzle -+", "Drizzle")]
    [InlineData("+- You have new mail from Kram -+", "Kram")]
    public void MailNotice_IsReadFromTheCapturedLine(string line, string from)
    {
        Assert.True(MailNotice.TryParse(line, out var sender));
        Assert.Equal(from, sender);
    }

    [Theory]
    [InlineData("Kayfez the pioneer says \"+- You have new mail from Drizzle -+\".")]
    [InlineData("\"+- You have new mail from Drizzle -+")]
    [InlineData("[Message number 14201 sent to Ollie]")]
    [InlineData("[You have mail: 1 item]")]
    public void MailNotice_RejectsEverythingButTheBareLine(string line)
        => Assert.False(MailNotice.TryParse(line, out _));
}
