using Mucka.Commands;

namespace Mucka.Util.Tests;

public sealed class ImportCommandArgsTests
{
    private const string Home = @"C:\Users\x";
    private const string Default = @"C:\Users\x\.muckaii\muckaii.db";

    private static ImportCommandArgs Parse(string argument, string? defaultPath = Default)
        => ImportCommandArgs.Parse(argument, Home, defaultPath, out _)!.Value;

    [Fact]
    public void NoArgument_UsesTheDefaultPath()
    {
        var args = Parse("  ");
        Assert.Equal(Default, args.Path);
        Assert.Empty(args.Unclosed);
    }

    [Fact]
    public void NoArgumentAndNoDefault_IsAnError()
    {
        Assert.Null(ImportCommandArgs.Parse("", Home, null, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("\"D:\\my dir\\m.db\"", "D:\\my dir\\m.db")]
    [InlineData("D:\\m.db", "D:\\m.db")]
    public void APath_IsTakenAsTyped_WithQuotesStripped(string argument, string expected)
        => Assert.Equal(expected, Parse(argument).Path);

    [Fact]
    public void ALeadingTilde_IsTheHomeDirectory()
        => Assert.Equal(Path.Combine(Home, ".muckaii/x.db"), Parse("~/.muckaii/x.db").Path);

    [Theory]
    [InlineData("unclosed 2,4", Default, new long[] { 2, 4 })]
    [InlineData("UNCLOSED 2 , 4", Default, new long[] { 2, 4 })]
    [InlineData("D:\\m.db unclosed 7", "D:\\m.db", new long[] { 7 })]
    public void UnclosedIds_AreSplitFromThePath(string argument, string path, long[] ids)
    {
        var args = Parse(argument);
        Assert.Equal(path, args.Path);
        Assert.True(args.Unclosed.SetEquals(ids));
    }

    [Fact]
    public void AWordEndingInUnclosed_IsNotTheKeyword()
        => Assert.Equal("D:\\notunclosed 2", Parse("D:\\notunclosed 2").Path);
}
