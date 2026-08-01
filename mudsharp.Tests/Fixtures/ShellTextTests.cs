using Mucka.Core.GuidedLogin;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Fixtures below are literal text fragments (whitespace/wrap artifacts included) lifted from the
/// two RESEARCH/mud-option-menu.*.jsonl captures, to keep the whitespace-agnostic parsing honest
/// against real server output rather than hand-tidied test strings.
/// </summary>
public class ShellTextTests
{
    [Theory]
    [InlineData("Type QUIT to abort login.\r\nAccount ID: ")]
    [InlineData("nothing landmark-y here at all")]
    public void LandmarksAreFalseForUnrelatedText(string text)
    {
        var n = ShellText.NormalizeWhitespace(text);
        Assert.False(ShellText.IsBannerSkipPrompt(n));
        Assert.False(ShellText.IsShellOptionPrompt(n));
        Assert.False(ShellText.IsExaminePrompt(n));
        Assert.False(ShellText.IsPersonaNamePrompt(n));
        Assert.False(ShellText.IsCreatingPersonaLine(n));
        Assert.False(ShellText.IsSexPrompt(n));
    }

    [Fact]
    public void DetectsBannerSkipPrompt()
    {
        var raw = "\u001B[0;7mSkip the rest? (y/n)\u001B[0m\u001B[0;37;40m n\b";
        Assert.True(ShellText.IsBannerSkipPrompt(ShellText.NormalizeWhitespace(raw)));
    }

    [Fact]
    public void DetectsShellOptionPrompt()
    {
        var raw = "MUD login menu.\r\nOption (H for help): ";
        Assert.True(ShellText.IsShellOptionPrompt(ShellText.NormalizeWhitespace(raw)));
    }

    [Fact]
    public void DetectsExaminePromptAndCommandsHelp()
    {
        var raw = "H for help.\r\nEXAMINE\u003E";
        Assert.True(ShellText.IsExaminePrompt(ShellText.NormalizeWhitespace(raw)));
    }

    [Fact]
    public void DetectsSexPromptAndCreatingPersonaLine()
    {
        var raw = "ggins\r\nCreating new persona.\r\u0000\r\nWhat sex do you wish to be?\r\n\u00FE\u00A7\u009B\u00FF\u00FF*\u00FF\u00FF";
        var n = ShellText.NormalizeWhitespace(raw);
        Assert.True(ShellText.IsCreatingPersonaLine(n));
        Assert.True(ShellText.IsSexPrompt(n));
    }

    [Fact]
    public void ParsesPersonaSlots_DotComCapture_WithUnusedSlot()
    {
        // Verbatim (minus ANSI) from mud-option-menu.dotcom.jsonl: an existing account with an
        // unused middle slot ("(1) Ollie, **Unused**, (2) Awlie.").
        var raw = "The personae available to you are:\r\u0000\r\n(1)     Ollie,\r\u0000\r\n" +
                  "        **Unused**,\r\u0000\r\n(2)     Awlie.\r\u0000\r\n" +
                  "By what name shall I call you (Q to quit)?\r\n";

        var slots = ShellText.TryParsePersonaSlots(ShellText.NormalizeWhitespace(raw));

        Assert.NotNull(slots);
        Assert.Equal(3, slots!.Count);
        Assert.Equal(new PersonaSlot(1, "Ollie", false), slots[0]);
        Assert.Equal(new PersonaSlot(2, null, true), slots[1]);
        Assert.Equal(new PersonaSlot(3, "Awlie", false), slots[2]);
    }

    [Fact]
    public void ParsesPersonaSlots_DotUkCapture_AllSlotsUsed()
    {
        // Verbatim (minus ANSI) from mud-option-menu.dotuk.jsonl: three full slots, no creation room.
        var raw = "The personae available to you are:\r\u0000\r\n(1)     Ollie,\r\u0000\r\n" +
                  "(2)     Flibble,\r\u0000\r\n(3)     Nessa.\r\u0000\r\n" +
                  "By what name shall I call you (Q to quit)?\r\n";

        var slots = ShellText.TryParsePersonaSlots(ShellText.NormalizeWhitespace(raw));

        Assert.NotNull(slots);
        Assert.Equal(3, slots!.Count);
        Assert.All(slots, s => Assert.False(s.IsUnused));
        Assert.Equal("Ollie", slots[0].Name);
        Assert.Equal("Flibble", slots[1].Name);
        Assert.Equal("Nessa", slots[2].Name);
    }

    [Fact]
    public void PersonaSlotsReturnsNullWhenLandmarkNotYetSeen()
    {
        Assert.Null(ShellText.TryParsePersonaSlots(ShellText.NormalizeWhitespace("Option (H for help): ")));
    }

    [Fact]
    public void ParsesExaminePersonae_DotComCapture()
    {
        var raw = "Account: P90003673\r\nBalance: 10hrs 0mins\r\nName: oliver Smith\r\n" +
                  "Terminal width: 100, MUDFREND version 1 compatible\r\nTimeout period: 5 minutes\r\n" +
                  "Personae:\r\n----------------------\r\nOllie      Score: 8691 Played: 27\r\n" +
                  "male       Str: 100 Dex: 100 Sta: 58 \r\n           Mag: 0 Max: 100\r\n" +
                  "           Tsk: none\r\n----------------------\r\nNaia       Score: 3928 Played: 5\r\n" +
                  "female     Str: 99 Dex: 100 Sta: 88 \r\n           Mag: 0 Max: 90\r\n" +
                  "           Tsk: none\r\n----------------------\r\nAwlie      Score: 2000 Played: 30\r\n" +
                  "male       Str: 85 Dex: 88 Sta: 97 \r\n           Mag: 0 Max: 97\r\n" +
                  "           Tsk: none\r\n----------------------\r\nH for help.\r\nEXAMINE\u003E";

        var personae = ShellText.ParseExaminePersonae(ShellText.NormalizeWhitespace(raw));

        Assert.Equal(3, personae.Count);
        Assert.Equal(new ExaminePersona("Ollie", "male", 8691, 27), personae[0]);
        Assert.Equal(new ExaminePersona("Naia", "female", 3928, 5), personae[1]);
        Assert.Equal(new ExaminePersona("Awlie", "male", 2000, 30), personae[2]);
    }

    [Fact]
    public void ParsesExaminePersonae_DotUkCapture_ScoreWithTaskList()
    {
        var raw = "Account: Z00012305\r\nName: kfsone (Web User)\r\n" +
                  "Terminal width: 100, MUDFREND version 1 compatible\r\nTimeout period: 5 minutes\r\n" +
                  "Personae:\r\n----------------------\r\nOllie      Score: 9252 Played: 11\r\n" +
                  "male       Str: 100 Dex: 100 Sta: 63 \r\n           Mag: 0 Max: 100\r\n" +
                  "           Tsk: 1 2 \r\n----------------------\r\nFlibble    Score: 2962 Played: 12\r\n" +
                  "male       Str: 92 Dex: 93 Sta: 85 \r\n           Mag: 0 Max: 85\r\n" +
                  "           Tsk: none\r\n----------------------\r\nNessa      Score: 7308 Played: 11\r\n" +
                  "female     Str: 96 Dex: 100 Sta: 80 \r\n           Mag: 0 Max: 100\r\n" +
                  "           Tsk: 6 \r\n----------------------\r\nH for help.\r\nEXAMINE\u003E";

        var personae = ShellText.ParseExaminePersonae(ShellText.NormalizeWhitespace(raw));

        Assert.Equal(3, personae.Count);
        Assert.Equal(new ExaminePersona("Ollie", "male", 9252, 11), personae[0]);
        Assert.Equal(new ExaminePersona("Flibble", "male", 2962, 12), personae[1]);
        Assert.Equal(new ExaminePersona("Nessa", "female", 7308, 11), personae[2]);
    }

    [Fact]
    public void NormalizeWhitespace_CollapsesWrapArtifactsAndTrims()
    {
        Assert.Equal("a b c", ShellText.NormalizeWhitespace("  a\r\u0000\r\nb\tc  "));
        Assert.Equal(string.Empty, ShellText.NormalizeWhitespace(""));
    }
}
