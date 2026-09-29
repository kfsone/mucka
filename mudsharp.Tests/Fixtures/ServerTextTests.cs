using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary><see cref="ServerText.Collapse"/>: rows the server wrapped read as the sentence they were
/// cut from. The rows are verbatim from a phone login at /T20.</summary>
public sealed class ServerTextTests
{
    [Fact]
    public void WrappedRows_ReadAsOneSentence()
        => Assert.Equal("You're already getting brief descriptions of fights.",
            ServerText.Collapse("You're already\r\0\r\ngetting brief\r\0\r\ndescriptions of\r\0\r\nfights.\r\0\r\n"));

    [Fact]
    public void Padding_AndRunsOfBlanks_AreOneSpace()
        => Assert.Equal("name: Kayfez", ServerText.Collapse("name:   \r\0 \t        Kayfez"));

    [Theory]
    [InlineData("")]
    [InlineData(" \r\0\r\n ")]
    public void NothingButBlanks_IsEmpty(string text)
        => Assert.Equal(string.Empty, ServerText.Collapse(text));
}
