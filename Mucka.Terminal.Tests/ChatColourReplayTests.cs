using MudSharp.Models;
using MudSharp.Protocol;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Chat colours and faces are looked up when a span is drawn (<see cref="CampbellPalette.ForegroundRgb"/>,
/// <see cref="ChatColorizer.Palette.FaceOf"/>), so a line held anywhere - the pane's buffer, the
/// pane history's main snapshot, the chat-mode replay - is drawn in the settings current at paint
/// time, exactly. The lines come from the
/// production parser and the ingest pass the view model runs, so what is held is what the pane
/// holds.
/// </summary>
public class ChatColourReplayTests
{
    private static readonly ChatColorizer.Palette Before = new(Emote: 0x112233, Say: 0x445566, Shout: 0x778899, Tell: 0xaabbcc, Faces: ChatFaces.Default);
    private static readonly ChatColorizer.Palette After  = new(Emote: 0x010203, Say: 0x040506, Shout: 0x070809, Tell: 0x0a0b0c,
        Faces: ChatFaces.Default with { ShoutOwn = ChatFace.Bold, SayOther = ChatFace.Italic | ChatFace.Dim, EmoteOther = ChatFace.Bold });

    // {09.00}Lazlo the yeoman says "{09.02}hi{pop}".{pop}, then your own shout and an emote.
    private static List<StyledLine> Ingest()
    {
        var parser = new MudStreamParser();
        var lines = new List<StyledLine>();
        parser.LineReady += lines.Add;
        static byte[] A(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        byte[] Message(string label, byte sub, string words) =>
            [0xA4, 0x9B, 0xFF, 0xFF, .. A(label + "\""), 0xA4, sub, 0xFF, 0xFF, .. A(words), 0xFF, 0xFF,
             .. A("\"."), 0xFF, 0xFF, .. A("\r\n")];
        parser.Feed(Message("Lazlo the yeoman says ", 0x9D, "hi"));
        parser.Feed(Message("You shout ", 0x9C, "help"));
        parser.Feed([0xA4, 0x9B, 0xFF, 0xFF, .. A("Lazlo "), 0xA4, 0xA0, 0x9D, 0xFF, 0xFF, .. A("dances"), 0xFF, 0xFF,
                     .. A("."), 0xFF, 0xFF, .. A("\r\n")]);

        var carry = default(ChatColorizer.Carry);
        return lines.Select(l => ChatColorizer.Apply(l, "Ollie", ref carry)).ToList();
    }

    private static TextStyle Span(IEnumerable<StyledLine> lines, string text)
        => lines.SelectMany(l => l.Spans).Single(s => s.Text == text).Style;

    [Fact]
    public void HeldLines_AreDrawnInTheCurrentSettings_InTheMainSnapshotAndTheChatReplay()
    {
        var history = new PaneHistory(mainCap: 50, chatCap: 50);
        foreach (var line in Ingest()) history.Append(line);

        foreach (var held in new[] { history.MainSnapshot(), history.ChatSnapshot() })
        {
            Assert.Equal(Before.Say,     CampbellPalette.ForegroundRgb(Span(held, "hi"), Before));
            Assert.Equal(Before.Shout,   CampbellPalette.ForegroundRgb(Span(held, "help"), Before));
            Assert.Equal(Before.Emote, CampbellPalette.ForegroundRgb(Span(held, "dances"), Before));

            // At the defaults: your words italic, nothing else styled.
            Assert.Equal(ChatFace.Italic, Before.FaceOf(Span(held, "help")));
            Assert.Equal(ChatFace.None,   Before.FaceOf(Span(held, "hi")));
            Assert.Equal(ChatFace.None,   Before.FaceOf(Span(held, "dances")));
            Assert.DoesNotContain(held.SelectMany(l => l.Spans), s => s.Style.Bold || s.Style.Italic);

            // The same held spans, changed settings: drawn in the new colours and faces, exactly.
            Assert.Equal(ChatColorizer.Dim(After.Say), CampbellPalette.ForegroundRgb(Span(held, "hi"), After));
            Assert.Equal(After.Shout,   CampbellPalette.ForegroundRgb(Span(held, "help"), After));
            Assert.Equal(After.Emote, CampbellPalette.ForegroundRgb(Span(held, "dances"), After));
            Assert.Equal(After.Emote, CampbellPalette.ForegroundRgb(Span(held, "Lazlo the yeoman says \""), After));
            Assert.Equal(ChatFace.Bold, After.FaceOf(Span(held, "help")));
            Assert.Equal(ChatFace.Italic | ChatFace.Dim, After.FaceOf(Span(held, "hi")));
            Assert.Equal(ChatFace.Bold, After.FaceOf(Span(held, "dances")));
            Assert.Equal(ChatFace.Bold, After.FaceOf(Span(held, "Lazlo the yeoman says \"")));
            Assert.Equal(ChatFace.None, After.FaceOf(Span(held, "You shout \"")));   // your label: EmoteOwn
        }
    }
}
