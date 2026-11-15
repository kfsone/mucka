using Mucka.Commands;

namespace Mucka.Util.Tests;

public sealed class ExportCommandArgsTests
{
    private const string Home = @"C:\Users\x";
    private const string Default = @"C:\Users\x\.mucka\mucka-export.db";

    private static ExportCommandArgs Parse(string argument)
        => ExportCommandArgs.Parse(argument, Home, Default);

    [Fact]
    public void NoArgument_UsesTheDefaultPath()
        => Assert.Equal(Default, Parse("  ").Path);

    [Theory]
    [InlineData("\"D:\\my dir\\m.db\"", "D:\\my dir\\m.db")]
    [InlineData("D:\\m.db", "D:\\m.db")]
    public void APath_IsTakenAsTyped_WithQuotesStripped(string argument, string expected)
        => Assert.Equal(expected, Parse(argument).Path);

    [Fact]
    public void ALeadingTilde_IsTheHomeDirectory()
        => Assert.Equal(Path.Combine(Home, ".mucka/x.db"), Parse("~/.mucka/x.db").Path);
}
