using Mucka.Core;

namespace MudSharp.Tests.Fixtures;

public class SessionCommandAliasesTests
{
    [Fact]
    public void DefinitionExpandsExistingAliasImmediately()
    {
        var aliases = CreateAliases();

        Define(aliases, "weap=mallet");
        Define(aliases, "km=k z with $weap");
        Define(aliases, "weap=axe");

        Assert.True(aliases.TryGet("km", out var command));
        Assert.Equal("k z with mallet", command);
    }

    [Fact]
    public void DefinitionLeavesUnknownReferencesForLaterExpansion()
    {
        var aliases = CreateAliases();

        Define(aliases, "km=k z with $weap");

        Assert.True(aliases.TryGet("km", out var command));
        Assert.Equal("k z with $weap", command);
    }

    [Fact]
    public void ClearRemovesDefinitions()
    {
        var aliases = CreateAliases();
        Define(aliases, "km=k z");

        aliases.Clear();

        Assert.False(aliases.TryGet("km", out _));
    }

    [Theory]
    [InlineData("=look")]
    [InlineData("two words=look")]
    [InlineData("2fast=look")]
    [InlineData("name=")]
    public void InvalidDefinitionReportsError(string definition)
    {
        var aliases = CreateAliases();

        Assert.True(aliases.TryDefine(definition, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("help=look")]
    [InlineData("map=look")]
    [InlineData("f12=look")]
    [InlineData("VER=look")]
    public void BuiltInCannotBeReassigned(string definition)
    {
        var aliases = CreateAliases();

        Assert.True(aliases.TryDefine(definition, out var name, out _, out var error));
        Assert.Contains("cannot replace built-in", error);
        Assert.False(aliases.TryGet(name, out _));
    }

    [Theory]
    [InlineData("x=say $help")]
    [InlineData("x=$map probe")]
    [InlineData("x=use $f1")]
    public void CommandBuiltInCannotBeUsedInDefinitionExpansion(string definition)
    {
        var aliases = CreateAliases();

        Assert.True(aliases.TryDefine(definition, out var name, out _, out var error));
        Assert.Contains("cannot use built-in", error);
        Assert.False(aliases.TryGet(name, out _));
    }

    [Fact]
    public void VersionBuiltInExpandsCaseSensitively()
    {
        var aliases = CreateAliases();

        Assert.Equal("say Mucka v0.14.0.98 and $ver",
            aliases.Expand("say $VER and $ver"));

        Define(aliases, "version=say $VER");
        Assert.True(aliases.TryGet("version", out var command));
        Assert.Equal("say Mucka v0.14.0.98", command);
    }

    [Theory]
    [InlineData("^1")]
    [InlineData("^2")]
    [InlineData("^3")]
    public void ControlAliasCanBeDefinedAndExpanded(string name)
    {
        var aliases = CreateAliases();
        Define(aliases, $"{name}=look");
        Define(aliases, $"wrapped=before {name} after");

        Assert.True(aliases.TryGet(name, out var command));
        Assert.Equal("look", command);
        Assert.True(aliases.TryGet("wrapped", out var wrapped));
        Assert.Equal("before look after", wrapped);
    }

    // GameViewModel.HandleCommand's "^N=command" guard relies on TryDefine trimming
    // whitespace around "=" itself (see issue #137) — lock that behavior down here so a
    // future change to either side doesn't silently break "^1 = look" / "^1= look" / "^1 =look".
    [Theory]
    [InlineData("^1=look")]
    [InlineData("^1 = look")]
    [InlineData("^1= look")]
    [InlineData("^1 =look")]
    [InlineData("  ^1  =  look  ")]
    public void ControlAliasDefinitionToleratesWhitespaceAroundEquals(string definition)
    {
        var aliases = CreateAliases();

        Assert.True(aliases.TryDefine(definition, out var name, out var command, out var error));
        Assert.Null(error);
        Assert.Equal("^1", name);
        Assert.Equal("look", command);
        Assert.True(aliases.TryGet("^1", out var stored));
        Assert.Equal("look", stored);
    }

    private static SessionCommandAliases CreateAliases() => new("0.14.0.98");

    private static void Define(SessionCommandAliases aliases, string definition)
    {
        Assert.True(aliases.TryDefine(definition, out _, out _, out var error));
        Assert.Null(error);
    }
}
