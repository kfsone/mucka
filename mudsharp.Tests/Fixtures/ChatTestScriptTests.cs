using System.Text.RegularExpressions;
using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="ChatTestScript"/>, the <c>$CHATTEST</c> script. Operator rules under test: 90 lines;
/// your says with their command echo; other Creatures saying, acting, shouting, screaming, yelling
/// and yodelling; exactly four tells, two each way; nothing sent to the server. The lines go
/// through the real parser, so what is asserted here is what the pane receives.
/// </summary>
public class ChatTestScriptTests
{
    private const string Me = "Ollie";

    private sealed record Run(
        List<StyledLine> Lines,
        List<(bool ChatOpen, bool FrameOpen)> StateAtNewline,
        List<string> Sounds,
        List<string> TellSenders,
        int OutgoingWrites,
        MudStreamParser Parser);

    private static Run Play(string me = Me)
    {
        var parser = new MudStreamParser();
        var lines = new List<StyledLine>();
        var state = new List<(bool, bool)>();
        var sounds = new List<string>();
        var senders = new List<string>();
        int outgoing = 0;
        parser.OutgoingBytes += _ => outgoing++;
        parser.TellReceived += senders.Add;
        ChatTestScript.Play(parser, me, line =>
        {
            lines.Add(line);
            if (!line.IsPartial) state.Add((parser.InChatContext, parser.C1.HasOpenColourFrame));
        }, sounds.Add);
        return new Run(lines, state, sounds, senders, outgoing, parser);
    }

    private static List<StyledLine> Complete(Run run) => run.Lines.Where(l => !l.IsPartial).ToList();

    private static bool IsEcho(StyledLine line)
        => line.Kind != LineKind.Chat
           && (line.PlainText.StartsWith('"') || line.PlainText.StartsWith("tell ", StringComparison.Ordinal)
               || line.PlainText.StartsWith("re ", StringComparison.Ordinal));

    private static bool IsMine(StyledLine line)
        => line.PlainText.StartsWith(Me + " " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal)
           || line.PlainText.StartsWith("You ", StringComparison.Ordinal);

    [Fact]
    public void Script_IsNinetyLines()
        => Assert.Equal(90, Complete(Play()).Count);

    [Fact]
    public void Script_EndsAtAShownPrompt()
    {
        var last = Play().Lines[^1];
        Assert.True(last.IsPartial);
        Assert.Equal("*", last.PlainText);
    }

    /// <summary>The script starts where a typed command finds the live parser: in game mode with its
    /// prompt already shown, so no scripted line carries a stray prompt of its own.</summary>
    [Fact]
    public void Script_StartsAtRest()
    {
        var lines = Play().Lines;
        Assert.DoesNotContain(lines, l => !l.IsPartial && l.PlainText.StartsWith('*'));
        Assert.False(lines[0].IsPartial);
    }

    [Fact]
    public void Script_HasExactlyFourTells_TwoEachWay()
    {
        var tells = Complete(Play()).Where(l => l.Spans.Any(s => s.Style.Speech == SpeechPart.Tell)).ToList();
        Assert.Equal(4, tells.Count);
        Assert.Equal(2, tells.Count(l => l.PlainText.StartsWith("You tell your listeners \"", StringComparison.Ordinal)));
        Assert.Equal(2, tells.Count(l => l.PlainText.Contains(" tells you \"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Script_CoversEveryKind()
    {
        var lines = Complete(Play());
        var chat = lines.Where(l => l.Kind == LineKind.Chat).ToList();
        int Mine(string verb) => chat.Count(l => IsMine(l) && l.PlainText.Contains($" {verb} \"", StringComparison.Ordinal));
        int Theirs(string verb) => chat.Count(l => !IsMine(l) && Regex.IsMatch(l.PlainText, $@"^[A-Z][^""]* {verb}\b"));

        Assert.True(Mine("says") >= 5, "your says");
        Assert.True(Mine("asks") >= 1 && Mine("exclaims") >= 1, "your asks and exclaims");
        Assert.Equal(Mine("says") + Mine("asks") + Mine("exclaims"),
            lines.Count(l => IsEcho(l) && l.PlainText.StartsWith('"')));
        foreach (var verb in new[] { "says", "asks", "exclaims", "whispers", "shouts", "yells", "screams", "yodels", "hollers" })
            Assert.True(Theirs(verb) >= 1, $"another Creature {verb}");
        Assert.True(Theirs("screams") >= 2 && Theirs("yodels") >= 2 && Theirs("shouts") >= 2 && Theirs("yells") >= 2);
        Assert.Contains(chat, l => l.PlainText.StartsWith("A male voice in the distance ", StringComparison.Ordinal));
        Assert.Contains(chat, l => l.PlainText.StartsWith("A female voice in the distance ", StringComparison.Ordinal));

        // Acts (09 04) and emotes (09 05) differ only by their code: counted in the bytes.
        var bytes = ChatTestScript.Bytes(Me);
        Assert.True(CountOf(bytes, [0xA4, 0x9F, 0xFF, 0xFF]) >= 3, "acts");
        Assert.True(CountOf(bytes, [0xA4, 0xA0]) >= 3, "emotes");
    }

    [Fact]
    public void EveryLine_ClosesItsColourFrames()
    {
        var run = Play();
        var lines = Complete(run);
        Assert.Equal(lines.Count, run.StateAtNewline.Count);
        // The one message the script wraps is the only line whose frames stay open at its newline;
        // an unclosed frame anywhere else would pass for a wrap, so the wrap is named, not inferred.
        var open = Enumerable.Range(0, lines.Count)
            .Where(i => run.StateAtNewline[i].ChatOpen || run.StateAtNewline[i].FrameOpen)
            .Select(i => lines[i].PlainText)
            .ToList();
        var wrapped = Assert.Single(open);
        Assert.StartsWith("Wargames the necromancer says \"", wrapped);
        Assert.False(run.Parser.InChatContext);
        Assert.False(run.Parser.C1.HasOpenColourFrame);
    }

    [Fact]
    public void Messages_AreChat_AndEchoesAreNot()
    {
        var lines = Complete(Play());
        var echoes = lines.Where(IsEcho).ToList();
        Assert.Equal(16, echoes.Count);   // 14 says, 2 tells
        Assert.All(echoes, e => Assert.Equal(LineKind.Normal, e.Kind));
        Assert.All(lines.Except(echoes), l => Assert.Equal(LineKind.Chat, l.Kind));
        Assert.Single(lines, l => l.ContinuesChat);
    }

    [Fact]
    public void AtTheDefaults_NothingIsBold_AndOnlyYourWordsAreItalic()
    {
        var carry = default(ChatColorizer.Carry);
        var p = ChatColorizer.DefaultPalette;
        bool previousMine = false;
        int mine = 0, theirs = 0, yourWords = 0;
        foreach (var line in Play().Lines)
        {
            var styled = ChatColorizer.Apply(line, Me, ref carry);
            Assert.All(styled.Spans, s => Assert.Equal(ChatFace.None, p.FaceOf(s.Style) & (ChatFace.Bold | ChatFace.Dim)));
            if (line.IsPartial || line.Kind != LineKind.Chat)
            {
                Assert.All(styled.Spans, s => Assert.Equal(ChatFace.None, p.FaceOf(s.Style)));
                continue;
            }
            bool isMine = line.ContinuesChat ? previousMine : IsMine(line);
            previousMine = isMine;
            if (isMine) mine++; else theirs++;
            foreach (var s in styled.Spans)
            {
                bool words = s.Style.Speech is SpeechPart.Say or SpeechPart.Shout or SpeechPart.Tell;
                bool italic = p.FaceOf(s.Style) == ChatFace.Italic;
                Assert.True(italic == (isMine && words), $"'{s.Text}' italic={italic}: {line.PlainText}");
                if (isMine && words) yourWords++;
            }
        }
        Assert.Equal(16, mine);   // 14 says, 2 tells
        Assert.True(theirs > 50);
        Assert.Equal(16, yourWords);
    }

    [Fact]
    public void Script_PlaysInGameMode_TellsDecoratedAndAlerted()
    {
        var run = Play();
        Assert.Equal(new[] { "Lazlo", "Atomicbob" }, run.TellSenders.Select(n => n.Split(' ')[0]));
        Assert.Equal(2, run.Sounds.Count);
        var yours = Complete(run).Where(l => l.PlainText.StartsWith("You tell your listeners", StringComparison.Ordinal));
        Assert.Equal(2, yours.Count());
        Assert.All(yours, l => Assert.DoesNotContain(l.Spans, s => s.Style.Italic || s.Style.Own));
    }

    [Fact]
    public void Script_SendsNothing()
        => Assert.Equal(0, Play().OutgoingWrites);

    [Fact]
    public void Bytes_AreOnlyTextAndCodes()
    {
        foreach (var b in ChatTestScript.Bytes(Me))
            Assert.True(b is 0x00 or 0x0A or 0x0D or (>= 0x20 and <= 0x7E) or >= 0x9B, $"byte 0x{b:x2}");
    }

    [Fact]
    public void AScriptedNameThatIsYours_SpeaksUnderAnother()
    {
        var lines = Complete(Play("Drizzle"));
        Assert.DoesNotContain(lines, l => l.PlainText.StartsWith("Drizzle ", StringComparison.Ordinal)
                                          && !l.PlainText.StartsWith("Drizzle " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal));
        Assert.Equal(14, lines.Count(l => l.PlainText.StartsWith("Drizzle " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal)));
        Assert.Equal(90, lines.Count);
    }

    /// <summary>In the wire log another Creature's unprompted line follows the standing prompt with
    /// nothing between; a second prompt after a bare pop never occurs.</summary>
    [Fact]
    public void AnotherCreaturesLine_FollowsThePromptDirectly()
    {
        var bytes = ChatTestScript.Bytes(Me);
        Assert.Equal(0, CountOf(bytes, ChatTestScript.Encode("{01}{01.02}*^^^^{01}")));
        Assert.True(CountOf(bytes, ChatTestScript.Encode("{01}{01.02}*^^{09.00}")) > 50);
    }

    [Fact]
    public void ATellToYourOwnName_GoesToTheSpareWhoReplies()
    {
        var lines = Complete(Play("Lazlo"));
        Assert.DoesNotContain(lines, l => l.PlainText.StartsWith("tell lazlo ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.PlainText.StartsWith("tell groggy \"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.PlainText.StartsWith("Groggy the yeoman tells you \"", StringComparison.Ordinal));
    }

    /// <summary>The slow run's units carry exactly the plain run's lines and sounds, in order.</summary>
    [Fact]
    public void Units_HoldThePlainRun_InOrder()
    {
        var plain = Play();
        var units = ChatTestScript.Units(Me);
        Assert.Equal(Complete(plain).Select(l => l.PlainText),
            units.SelectMany(u => u.Lines).Where(l => !l.IsPartial).Select(l => l.PlainText));
        Assert.Equal(plain.Sounds, units.SelectMany(u => u.Sounds));
    }

    /// <summary>A unit is one scripted entry: it ends at the script's prompt, holds one message
    /// (after your echo, when it is yours), and the wrapped message keeps its rows together.</summary>
    [Fact]
    public void Units_AreOneEntryEach_EndingAtThePrompt()
    {
        var units = ChatTestScript.Units(Me);
        Assert.Equal(73, units.Count);   // 90 lines: 16 echoes and one wrapped row ride with their message
        Assert.All(units, u =>
        {
            Assert.True(u.Lines[^1].IsPartial);
            Assert.Equal("*", u.Lines[^1].PlainText);
            var complete = u.Lines.Where(l => !l.IsPartial).ToList();
            var echoes = complete.Count(IsEcho);
            Assert.True(echoes <= 1);
            if (echoes == 1) Assert.True(IsEcho(complete[0]));
            Assert.Equal(1, complete.Count(l => l.Kind == LineKind.Chat && !l.ContinuesChat));
        });
        var wrapped = Assert.Single(units, u => u.Lines.Any(l => l.ContinuesChat));
        Assert.StartsWith("Wargames the necromancer says \"", wrapped.Lines.First(l => !l.IsPartial).PlainText);
        Assert.Equal(2, units.Count(u => u.Sounds.Count > 0));
    }

    [Fact]
    public void Units_SendNothing()
    {
        var parser = new MudStreamParser();
        int outgoing = 0;
        parser.OutgoingBytes += _ => outgoing++;
        ChatTestScript.Units(parser, Me);
        Assert.Equal(0, outgoing);
    }

    private static int CountOf(byte[] haystack, byte[] needle)
    {
        int n = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) n++;
        return n;
    }
}
