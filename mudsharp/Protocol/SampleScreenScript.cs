using System.Text;
using MudSharp.Models;

namespace MudSharp.Protocol;

/// <summary>
/// The Text size page's example screen (docs/font-setup-design.md): a room and some chat, as the
/// raw bytes the server sends, played through a <see cref="MudStreamParser"/> of its own so the
/// lines carry the styles and chat parts the live pane draws. Written in
/// <see cref="ChatTestScript"/>'s shape notation and encoded by it.
///
/// <para>Wire shapes, from the <c>wire</c> table: a room arrives as
/// <c>{20.01}{02.01}Elizabethan tearoom^.|^{02.02}This cosy, Tudor period room ...^|</c> with the
/// description wrapped by the server, then one <c>{05.00.01}</c> frame per mortal here, an object
/// they hold in <c>{03.01.00}a cup of tea^</c>, then the prompt <c>{01}{01.02}*^^</c>. The chat
/// lines take the shapes <see cref="ChatTestScript"/> documents. Every word is invented.</para>
/// </summary>
public static class SampleScreenScript
{
    private const string Prompt = "{01}{01.02}*^^";

    private static readonly string[] Script =
    {
        "{00}" + Prompt,
        "{20.01}{02.01}Mucky tearoom^.|"
            + "^{02.02}You are in a small, warm tearoom that smells of fresh scones. A door leads out to|"
            + "the north, and a narrow staircase climbs to the floor above. ^|"
            + "{05.00.01}Ollie the amateur is here holding {03.01.00}a cup of tea^.^|"
            + "{05.00.01}Drizzle the wobbly mage is out with other humans.^|"
            + Prompt,
        "{09.00}Lady Polly the witch asks \"{09.02}Where did I leave my dragon?^\".^|" + Prompt,
        "{09.00}A male voice in the distance shouts \"{09.01}I found the ram :(^\".^|" + Prompt,
        "{09.00}Drizzle the wobbly mage tells you \"{09.03}He could have bought cheap ram from me^\".^|" + Prompt,
        // No prompt after the last line: a lone '*' row at the bottom of an example reads as noise.
        "{09.00}Mucka {09.04}acts like it's waiting for your selection.^^|",
    };

    /// <summary>The example screen's lines, chat coloured for nobody in particular.</summary>
    public static IReadOnlyList<StyledLine> Lines()
    {
        var parser = new MudStreamParser();
        var lines = new List<StyledLine>();
        var carry = default(ChatColorizer.Carry);
        void OnLine(StyledLine line) => lines.Add(ChatColorizer.Apply(line, null, ref carry));
        parser.EnterGameMode();
        parser.LineReady += OnLine;
        try
        {
            var dsl = new StringBuilder();
            foreach (var part in Script)
                dsl.Append(part);
            parser.Feed(ChatTestScript.Encode(dsl.ToString()));
        }
        finally
        {
            parser.LineReady -= OnLine;
        }
        return lines;
    }
}
