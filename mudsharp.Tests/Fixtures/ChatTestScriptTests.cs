using System.Text.RegularExpressions;
using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="ChatTestScript"/>, the <c>$CHATTEST</c> script. Operator rules under test: 50 lines
/// covering every kind; your say, shout-family, tell and emote lines, each after its command echo;
/// other Creatures saying, acting, emoting, shouting, screaming and yodelling; "Someone" and
/// "Someone powerful" lines; exactly six tells, two yours and four to you, two of those from
/// Someone; nothing sent to the server. The lines go through the real parser, so what is asserted
/// here is what the pane receives.
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

    private static readonly string[] EchoLeads = { "\"", "sh \"", "yell ", "scream ", "tell ", "re \"" };

    private static bool IsEcho(StyledLine line)
        => line.Kind != LineKind.Chat
           && (EchoLeads.Any(lead => line.PlainText.StartsWith(lead, StringComparison.Ordinal)) || line.PlainText == "sigh");

    private static bool IsMine(StyledLine line)
        => line.PlainText.StartsWith(Me + " " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal)
           || line.PlainText.StartsWith("OK, " + Me + " " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal)
           || line.PlainText.StartsWith("You ", StringComparison.Ordinal);

    [Fact]
    public void Script_IsFiftyLines()
        => Assert.Equal(50, Complete(Play()).Count);

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
    public void Script_HasExactlySixTells_TwoYours_FourToYou_TwoOfThemFromSomeone()
    {
        var tells = Complete(Play()).Where(l => l.Spans.Any(s => s.Style.Speech == SpeechPart.Tell)).ToList();
        Assert.Equal(6, tells.Count);
        Assert.Equal(2, tells.Count(l => l.PlainText.StartsWith("You tell your listeners \"", StringComparison.Ordinal)));
        Assert.Equal(4, tells.Count(l => l.PlainText.Contains(" tells you \"", StringComparison.Ordinal)));
        Assert.Equal(2, tells.Count(l => l.PlainText.StartsWith("Someone tells you \"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Script_CoversEveryKind()
    {
        var lines = Complete(Play());
        var chat = lines.Where(l => l.Kind == LineKind.Chat).ToList();
        int Mine(string verb) => chat.Count(l => IsMine(l) && l.PlainText.Contains($" {verb} \"", StringComparison.Ordinal));
        int Theirs(string verb) => chat.Count(l => !IsMine(l) && Regex.IsMatch(l.PlainText, $@"^[A-Z][^""]* {verb}\b"));
        int Echoes(string lead) => lines.Count(l => IsEcho(l) && l.PlainText.StartsWith(lead, StringComparison.Ordinal));

        // Yours: every channel, each message after its echo.
        foreach (var verb in new[] { "says", "asks", "exclaims" })
            Assert.True(Mine(verb) >= 1, $"your {verb}");
        Assert.Equal(Mine("says") + Mine("asks") + Mine("exclaims"), Echoes("\""));
        foreach (var (verb, lead) in new[] { ("shout", "sh \""), ("yell", "yell "), ("scream", "scream ") })
        {
            Assert.Equal(1, Mine(verb));
            Assert.Equal(1, Echoes(lead));
        }
        Assert.Equal(1, Echoes("sigh"));
        Assert.Single(chat, l => l.PlainText == "OK, " + Me + " " + ChatTestScript.MyTitle + " sighs.");

        // Theirs. A yell and a scream with words are in the wire log only as your own.
        foreach (var verb in new[] { "says", "asks", "exclaims", "whispers", "shouts", "screams", "yodels", "hollers", "cheers", "howls" })
            Assert.True(Theirs(verb) >= 1, $"another Creature {verb}");
        Assert.True(Theirs("screams") >= 2 && Theirs("yodels") >= 2 && Theirs("shouts") >= 3);
        Assert.Contains(chat, l => Regex.IsMatch(l.PlainText, @"^\S+ the [^""]* yodels """));
        Assert.Contains(chat, l => l.PlainText == "A male voice in the distance yodels.");
        Assert.Contains(chat, l => l.PlainText.StartsWith("A male voice in the distance ", StringComparison.Ordinal));
        Assert.Contains(chat, l => l.PlainText.StartsWith("A female voice in the distance ", StringComparison.Ordinal));

        // The unseen: Someone's tells and hello, Someone powerful's emote.
        Assert.Equal(2, chat.Count(l => l.PlainText.StartsWith("Someone tells you \"", StringComparison.Ordinal)));
        Assert.Single(chat, l => l.PlainText == "Someone bids you morning.");
        Assert.Single(chat, l => l.PlainText.StartsWith("Someone powerful ", StringComparison.Ordinal));

        // Acts (09 04), emotes (09 05 0n) and the hello (09 07) differ only by their code: counted
        // in the bytes. Emotes include yours.
        var bytes = ChatTestScript.Bytes(Me);
        Assert.True(CountOf(bytes, [0xA4, 0x9F, 0xFF, 0xFF]) >= 3, "acts");
        foreach (byte kind in new byte[] { 0x9B, 0x9C, 0x9D })
            Assert.True(CountOf(bytes, [0xA4, 0xA0, kind, 0xFF, 0xFF]) >= 1, $"emote 09 05 {kind - 0x9B:00}");
        Assert.Equal(1, CountOf(bytes, [0xA4, 0xA2, 0xFF, 0xFF]));
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
        Assert.Equal(10, echoes.Count);   // 4 says, a shout, a yell, a scream, 2 tells, an emote
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
        Assert.Equal(10, mine);   // 4 says, a shout, a yell, a scream, 2 tells, an emote
        Assert.Equal(30, theirs);
        Assert.Equal(9, yourWords);   // the emote has no words
    }

    [Fact]
    public void Script_PlaysInGameMode_TellsDecoratedAndAlerted()
    {
        var run = Play();
        Assert.Equal(new[] { "Lazlo", "Atomicbob" }, run.TellSenders.Select(n => n.Split(' ')[0]));
        Assert.Equal(new[] { "sounds/tell.wav", "sounds/tell-invis.wav", "sounds/tell.wav", "sounds/tell-invis.wav" }, run.Sounds);
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
        Assert.Equal(4, lines.Count(l => l.PlainText.StartsWith("Drizzle " + ChatTestScript.MyTitle + " ", StringComparison.Ordinal)));
        Assert.Equal(50, lines.Count);
    }

    /// <summary>In the wire log another Creature's unprompted line follows the standing prompt with
    /// nothing between; a second prompt after a bare pop never occurs.</summary>
    [Fact]
    public void AnotherCreaturesLine_FollowsThePromptDirectly()
    {
        var bytes = ChatTestScript.Bytes(Me);
        Assert.Equal(0, CountOf(bytes, ChatTestScript.Encode("{01}{01.02}*^^^^{01}")));
        // All 29 of another Creature's lines but the first, which follows the opening prompt.
        Assert.Equal(28, CountOf(bytes, ChatTestScript.Encode("{01}{01.02}*^^{09.00}")));
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
        Assert.Equal(39, units.Count);   // 50 lines: 10 echoes and one wrapped row ride with their message
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
        Assert.Equal(4, units.Count(u => u.Sounds.Count > 0));
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
