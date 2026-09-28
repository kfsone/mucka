using System.Text.RegularExpressions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Operator rule: <c>$CHATTEST</c> sends nothing to the server. GameViewModel is in the MAUI app and
/// no suite can construct it, so this reads its source: the command is dispatched inside the "$"
/// block of HandleCommand (which returns true, so the line is never sent), and neither PlayChatTest
/// nor the ShowLine it feeds touches the connection. The source is compared with all whitespace
/// removed, so a reformat does not break it.
/// </summary>
public class ChatTestCommandSendsNothingTests
{
    private static string ViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Squash(File.ReadAllText(Path.Combine(dir.FullName, "ViewModels", "GameViewModel.cs")));
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", "");

    private static string BodyOf(string source, string signature)
    {
        signature = Squash(signature);
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"{signature} has no closing brace");
    }

    [Theory]
    [InlineData("void RunChatTest(string argument)")]
    [InlineData("void PlayChatTest(string myName)")]
    [InlineData("void PlaySlowChatTest(string myName)")]
    [InlineData("void ShowChatTestUnit(ChatTestUnit unit)")]
    [InlineData("bool StopSlowChatTest(string why)")]
    [InlineData("void ShowLine(StyledLine line)")]
    public void NeverTouchesTheConnection(string signature)
    {
        var body = BodyOf(ViewModelSource(), signature);
        Assert.DoesNotContain("_conn", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Send", body, StringComparison.Ordinal);
    }

    /// <summary>The notes the command makes reach the connection only as a wire-log annotation.</summary>
    [Fact]
    public void Notes_ReachTheConnection_OnlyAsAnAnnotation()
    {
        var body = BodyOf(ViewModelSource(), "void InjectNote(string annotation)");
        Assert.Equal(Regex.Count(body, "_conn"), Regex.Count(body, @"_conn\.Annotate\("));
        Assert.DoesNotContain("Send", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayChatTest_PlaysTheScript()
        => Assert.Contains("ChatTestScript.Play(", BodyOf(ViewModelSource(), "void PlayChatTest(string myName)"), StringComparison.Ordinal);

    /// <summary>The slow run leads with its note, and each unit goes through ShowLine.</summary>
    [Fact]
    public void PlaySlowChatTest_NotesFirst_ThenPacesTheUnits()
    {
        var source = ViewModelSource();
        var body = BodyOf(source, "void PlaySlowChatTest(string myName)");
        int note = body.IndexOf("InjectNote(\"//$CHATTESTslow:", StringComparison.Ordinal);
        int start = body.IndexOf("_slowChatTest.Start(ChatTestScript.Units(myName),ShowChatTestUnit,", StringComparison.Ordinal);
        Assert.True(note >= 0 && start > note, "note, then start");
        Assert.Contains("$CHATTESTstopstopsit", body, StringComparison.Ordinal);
        Assert.Contains("ShowLine(", BodyOf(source, "void ShowChatTestUnit(ChatTestUnit unit)"), StringComparison.Ordinal);
    }

    /// <summary>Operator rule: a slow run never outlives the login or the connection.</summary>
    [Theory]
    [InlineData("void OnGameModeExited()", "StopSlowChatTest(")]
    [InlineData("void ResetSessionState()", "StopSlowChatTest(")]
    [InlineData("ValueTask DisposeAsync()", "_slowChatTest.Stop();")]
    public void ASlowRun_EndsWithTheSession(string signature, string stop)
        => Assert.Contains(Squash(stop), BodyOf(ViewModelSource(), signature), StringComparison.Ordinal);

    /// <summary>Either form of the command stops a slow run before it plays.</summary>
    [Fact]
    public void Starting_StopsTheRunningOne_First()
    {
        var body = BodyOf(ViewModelSource(), "void RunChatTest(string argument)");
        int stop = body.IndexOf("StopSlowChatTest(\"stopped,startingagain\");", StringComparison.Ordinal);
        Assert.True(stop >= 0, "stops first");
        Assert.True(body.IndexOf("PlaySlowChatTest(", StringComparison.Ordinal) > stop);
        Assert.True(body.IndexOf("PlayChatTest(", StringComparison.Ordinal) > stop);
    }

    [Fact]
    public void ChatTest_IsDispatchedInsideTheDollarBlock_WhichNeverSends()
    {
        var handle = BodyOf(ViewModelSource(), "bool HandleCommand(string text)");
        var dollar = BodyOf(handle, "if (text.StartsWith('$'))");
        Assert.Contains("\"CHATTEST\",StringComparison.OrdinalIgnoreCase)", dollar, StringComparison.Ordinal);
        Assert.Contains("name.StartsWith(\"CHATTEST\",StringComparison.OrdinalIgnoreCase)", dollar, StringComparison.Ordinal);
        Assert.Contains("RunChatTest(", dollar, StringComparison.Ordinal);
        Assert.EndsWith("returntrue;}", dollar, StringComparison.Ordinal);
    }
}
