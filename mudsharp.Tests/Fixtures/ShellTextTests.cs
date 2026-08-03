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
        Assert.False(ShellText.IsYesNoPrompt(n));
        Assert.False(ShellText.IsShellOptionPrompt(n));
        Assert.False(ShellText.IsExaminePrompt(n));
        Assert.False(ShellText.IsPersonaNamePrompt(n));
        Assert.False(ShellText.IsDatabaseStillInitialisingLine(n));
        Assert.False(ShellText.IsDatabaseStartedInitialisingLine(n));
        Assert.False(ShellText.IsDatabaseFinishedInitialisingLine(n));
        Assert.False(ShellText.IsCreatingPersonaLine(n));
        Assert.False(ShellText.IsSexPrompt(n));
        Assert.False(ShellText.IsNotUpdatingPersonaLine(n));
    }

    [Fact]
    public void DetectsYesNoPrompt_SkipTheRestAndUsurp()
    {
        var skip = "\u001B[0;7mSkip the rest? (y/n)\u001B[0m\u001B[0;37;40m n\b";
        Assert.True(ShellText.IsYesNoPrompt(ShellText.NormalizeWhitespace(skip)));

        var usurp = "That account is already logged in. Usurp the existing session? (y/n)";
        Assert.True(ShellText.IsYesNoPrompt(ShellText.NormalizeWhitespace(usurp)));
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
    public void DetectsDatabaseInitialisingLifecycleMessages()
    {
        Assert.True(ShellText.IsDatabaseStillInitialisingLine(
            ShellText.NormalizeWhitespace("p\r\nThe database is still initialising.\r\nOption (H for help): ")));
        Assert.True(ShellText.IsDatabaseStartedInitialisingLine(
            ShellText.NormalizeWhitespace("\r\n+- The database has started initialising -+\r\n")));
        Assert.True(ShellText.IsDatabaseFinishedInitialisingLine(
            ShellText.NormalizeWhitespace("\r\n+- The database has finished initialising -+\r\n")));
    }

    [Fact]
    public void DetectsNotUpdatingPersonaLine()
    {
        var raw = "You have been killed by someone.\r\u0000\r\n\u00A3\u00A8\u00FF\u00FFNot updating persona.\u00FF\u00FF\r\u0000\r\n \r\n";
        Assert.True(ShellText.IsNotUpdatingPersonaLine(ShellText.NormalizeWhitespace(raw)));
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
    public void ParsesPersonaSlots_AfterGameReset_SkipsTheReprintedBanner()
    {
        // Verbatim (minus ANSI) from RESEARCH/game-reset.jsonl: once the rebuilt database is up,
        // "p" replies with the whole login banner again before the persona list. The dated
        // "MUD last reset on 2-AUG-2026 at 20:19:05." line sits above the start landmark and must
        // not be mistaken for a slot.
        var raw = "p\r\nMUD version 4E.\r\nCopyright (C) 1991-2026\r\nMulti-User Entertainment Ltd.\r\n" +
                  "Licensed (number 57009120) to Richard Underwood.\r\n\r\n" +
                  "+- Please change your password. Type /P at the \"*\" prompt. -+\r\u0000\r\n" +
                  "Your last game of MUD began on 2-AUG-2026 at 19:54:50.\r\u0000\r\n" +
                  "MUD last reset on 2-AUG-2026 at 20:19:05.\r\u0000\r\n" +
                  "This reset is number 126509.\r\u0000\r\n\r\n" +
                  "The personae available to you are:\r\u0000\r\n(1)     Ollie,\r\u0000\r\n" +
                  "(2)     Shezerah,\r\u0000\r\n(3)     Nessa.\r\u0000\r\n" +
                  "By what name shall I call you (Q to quit)?\r\n";

        var normalized = ShellText.NormalizeWhitespace(raw);
        Assert.True(ShellText.IsPersonaNamePrompt(normalized));

        var slots = ShellText.TryParsePersonaSlots(normalized);

        Assert.NotNull(slots);
        Assert.Equal(new[] { "Ollie", "Shezerah", "Nessa" }, slots!.Select(s => s.Name));
        Assert.All(slots, s => Assert.False(s.IsUnused));
    }

    [Fact]
    public void PlayIsRefusedWhileTheDatabaseRebuilds_ButTheOptionPromptStillLands()
    {
        // The reset-time "p" reply: no persona list, just the refusal and a fresh Option prompt.
        // Guided login has to tell this apart from a real slot list and keep retrying.
        var n = ShellText.NormalizeWhitespace("p\r\nThe database is still initialising.\r\nOption (H for help): ");

        Assert.True(ShellText.IsDatabaseStillInitialisingLine(n));
        Assert.True(ShellText.IsShellOptionPrompt(n));
        Assert.False(ShellText.IsPersonaNamePrompt(n));
        Assert.Null(ShellText.TryParsePersonaSlots(n));
    }

    [Fact]
    public void OurOwnEchoedCommandMatchesTheOptionPrompt_SoItCannotBeUsedAsAReplyLandmark()
    {
        // The shell echoes what we type onto the prompt line itself, and a partial line is
        // re-published every time it grows, so this lands in a freshly cleared buffer the instant
        // we send "p". Guided login must not read it as "the shell answered": it once did, threw
        // away the persona list arriving behind it, and re-sent "p" into the name prompt, which the
        // shell answered with 'Sorry, I can't call you "P".'
        var n = ShellText.NormalizeWhitespace("Option (H for help): p");

        Assert.True(ShellText.IsShellOptionPrompt(n));
        Assert.False(ShellText.IsPersonaNamePrompt(n));
        Assert.False(ShellText.IsDatabaseStillInitialisingLine(n));
        Assert.False(ShellText.IsDatabaseStartedInitialisingLine(n));
        Assert.False(ShellText.IsDatabaseFinishedInitialisingLine(n));
    }

    [Fact]
    public void OptionUnavailableNoiseIsRecognisedButIsNotAPromptWeActOn()
    {
        // What the shell says when it is handed something it cannot parse -- a bare CR, or an
        // unterminated FES probe that was in flight when the server dropped us to the menu and got
        // flushed. Guided login has to read straight past it.
        var n = ShellText.NormalizeWhitespace(
            "MUD login menu.\r\nOption (H for help): Option unavailable.\r\nOption (H for help): ");

        Assert.True(ShellText.IsOptionUnavailableLine(n));
        Assert.True(ShellText.IsShellOptionPrompt(n));
        Assert.False(ShellText.IsPersonaNamePrompt(n));
        Assert.False(ShellText.IsNameRejectedPrompt(n));
        Assert.Null(ShellText.TryParsePersonaSlots(n));
    }

    [Fact]
    public void ParsesPersonaSlots_ThroughInterleavedOptionUnavailableNoise()
    {
        // The whole re-entry exchange as the player saw it: an in-flight FES probe drawing an
        // "Option unavailable.", the flush prompt, our echoed "p", then the reprinted banner and the
        // real list. The junk sits between the landmarks and must not disturb the parse.
        var raw = "Option (H for help): Option unavailable.\r\nOption (H for help): \r\n" +
                  "Option (H for help): p\r\nMUD version 4E.\r\nCopyright (C) 1991-2026\r\n" +
                  "Multi-User Entertainment Ltd.\r\n\r\n" +
                  "The personae available to you are:\r \r\n(1)     Ollie,\r \r\n" +
                  "(2)     Shezerah,\r \r\n(3)     Nessa.\r \r\n" +
                  "By what name shall I call you (Q to quit)?\r\n";

        var n = ShellText.NormalizeWhitespace(raw);

        Assert.True(ShellText.IsPersonaNamePrompt(n));
        var slots = ShellText.TryParsePersonaSlots(n);
        Assert.NotNull(slots);
        Assert.Equal(new[] { "Ollie", "Shezerah", "Nessa" }, slots!.Select(s => s.Name));
    }

    [Fact]
    public void DetectsNameRejectedPrompt_AndDoesNotConfuseItWithTheFirstNamePrompt()
    {
        // Something got typed into the name prompt ahead of our answer (the same in-flight probe),
        // so the shell refuses it and re-asks. Still a name prompt: the answer is a persona name.
        var rejected = ShellText.NormalizeWhitespace(
            "*p\r\nSorry, I can't call you \"P\".\r\nWhat shall I call you instead?\r\n");
        Assert.True(ShellText.IsNameRejectedPrompt(rejected));
        Assert.False(ShellText.IsPersonaNamePrompt(rejected));

        var firstAsk = ShellText.NormalizeWhitespace("By what name shall I call you (Q to quit)?\r\n");
        Assert.False(ShellText.IsNameRejectedPrompt(firstAsk));
        Assert.True(ShellText.IsPersonaNamePrompt(firstAsk));
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

    [Fact]
    public void ExtractSplash_SkipsMotdAndYesNoPrompt_StopsAtCheckingMail()
    {
        // mud2.com style: login line, dated MOTD notice, "Skip the rest? (y/n)" + echoed answer,
        // then the real banner, then "[Checking mail...]" and the rest of the shell landing text.
        // The whole MOTD/notice block is discarded along with the prompt itself -- only the real
        // banner (from right after the prompt) is kept.
        var lines = new List<string>
        {
            "P90003673 logged in on pts/13.",
            "****11-MAR-26****20:32:23****",
            "********From: Viktor*********",
            "Software update: some notice text.",
            "*****************************",
            "Skip the rest? (y/n)",
            "y",
            "                    ___\\_\\_\\_\\_  (c) 2026 MUSE Ltd.",
            " [P]  Play the game",
            " [Q]  Quit",
            "[Checking mail...]",
            "[You have no mail]",
            "MUD login menu.",
            "Option (H for help): ",
        };

        var splash = ShellText.ExtractSplash(lines);

        Assert.NotNull(splash);
        Assert.DoesNotContain("logged in on", splash);
        Assert.DoesNotContain("Skip the rest", splash);
        Assert.DoesNotContain("Software update", splash);
        Assert.DoesNotContain("From: Viktor", splash);
        Assert.DoesNotContain("Checking mail", splash);
        Assert.Contains("MUSE Ltd.", splash);
        Assert.Contains("[P]  Play the game", splash);
        Assert.Contains("[Q]  Quit", splash);
    }

    [Fact]
    public void ExtractSplash_NoYesNoPrompt_DotUkStyle()
    {
        // mud2.co.uk style: straight from "logged in on" into the banner, no skip/usurp prompt.
        var lines = new List<string>
        {
            "Z00012305 logged in on pts/3.",
            "                     .oooooooo.",
            " (XXXXX)      (XXXX)(XXXXX)     |XXX|(XXXXXXXXXXX\\    [P] - Play MUD2.",
            "[Checking mail...]",
            "[You have no mail]",
            "MUD login menu.",
            "Option (H for help): ",
        };

        var splash = ShellText.ExtractSplash(lines);

        Assert.NotNull(splash);
        Assert.DoesNotContain("logged in on", splash);
        Assert.DoesNotContain("Checking mail", splash);
        Assert.Contains("Play MUD2.", splash);
    }

    [Fact]
    public void ExtractSplash_UsurpPrompt_DiscardsPrecedingNotice()
    {
        var lines = new List<string>
        {
            "P90003673 logged in on pts/13.",
            "That account is already logged in.",
            "Usurp the existing session? (y/n)",
            "y",
            "                    ___\\_\\_\\_\\_  (c) 2026 MUSE Ltd.",
            "[Checking mail...]",
            "Option (H for help): ",
        };

        var splash = ShellText.ExtractSplash(lines);

        Assert.NotNull(splash);
        Assert.DoesNotContain("already logged in", splash);
        Assert.DoesNotContain("Usurp", splash);
        Assert.Contains("MUSE Ltd.", splash);
    }

    [Fact]
    public void ExtractSplash_ReturnsNullWhenNothingBetweenLandmarks()
    {
        var lines = new List<string>
        {
            "P90003673 logged in on pts/13.",
            "[Checking mail...]",
            "Option (H for help): ",
        };

        Assert.Null(ShellText.ExtractSplash(lines));
    }
}
