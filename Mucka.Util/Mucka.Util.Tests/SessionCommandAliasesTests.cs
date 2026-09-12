using Mucka.Commands;

namespace Mucka.Util.Tests;

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
    // whitespace around "=" itself - lock that behavior down here so a future change to
    // either side doesn't silently break "^1 = look" / "^1= look" / "^1 =look".
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

    /// <summary>A worked example of positional-slot filling, verbatim.</summary>
    [Fact]
    public void PositionalSlots_AreFilledFromTheArguments()
    {
        var aliases = CreateAliases();
        Define(aliases, "k=ql $1,k $1 wi $2");

        Assert.Equal("ql rat0,k rat0 wi axe", aliases.Expand("$k rat0 axe"));
    }

    /// <summary>A body with no slots keeps the behaviour it has always had - the reference is
    /// replaced in place and the rest of the line is left alone, which appends.</summary>
    [Fact]
    public void ABodyWithoutSlots_StillAppendsItsArguments()
    {
        var aliases = CreateAliases();
        Define(aliases, "g=get");

        Assert.Equal("get sword", aliases.Expand("$g sword"));
    }

    /// <summary>Arguments past the highest slot the body mentions are appended, so a one-slot body
    /// still behaves like the old form for anything extra.</summary>
    [Fact]
    public void ArgumentsBeyondTheHighestSlot_AreAppended()
    {
        var aliases = CreateAliases();
        Define(aliases, "q=ql $1");

        Assert.Equal("ql rat0 and rat3", aliases.Expand("$q rat0 and rat3"));
    }

    /// <summary>A slot with no argument expands to nothing, as a shell does. The result can be a
    /// malformed command, which is the point: the player sees what was sent.</summary>
    [Fact]
    public void AMissingArgument_ExpandsToNothing()
    {
        var aliases = CreateAliases();
        Define(aliases, "k=k $1 wi $2");

        Assert.Equal("k rat0 wi ", aliases.Expand("$k rat0"));
    }

    /// <summary>Slots survive definition intact - there are no arguments at definition time, and
    /// $1 has never matched the alias-reference pattern.</summary>
    [Fact]
    public void SlotsAreStoredUnexpanded()
    {
        var aliases = CreateAliases();
        Define(aliases, "k=ql $1,k $1 wi $2");

        Assert.True(aliases.TryGet("k", out var stored));
        Assert.Equal("ql $1,k $1 wi $2", stored);
    }

    /// <summary>Only a reference at the START of the line takes arguments: there is no rule for
    /// which words belong to which reference once there can be several. Mid-line it stays an
    /// ordinary inline replacement, and an unfilled slot is left standing rather than quietly
    /// dropped - the player sees a literal $1 in what was sent and learns the slot form is
    /// line-leading, which a silent empty string would not teach them.</summary>
    [Fact]
    public void AMidLineReference_ExpandsInlineAndLeavesItsSlotsStanding()
    {
        var aliases = CreateAliases();
        Define(aliases, "k=k $1");

        Assert.Equal("say hello k $1 rat0", aliases.Expand("say hello $k rat0"));
    }

    /// <summary>An argument is text, not a reference - expanding it would make the result depend on
    /// which aliases happen to exist when it is used.</summary>
    [Fact]
    public void ArgumentsAreNotThemselvesExpanded()
    {
        var aliases = CreateAliases();
        Define(aliases, "weap=axe");
        Define(aliases, "k=k $1 wi $2");

        Assert.Equal("k rat0 wi $weap", aliases.Expand("$k rat0 $weap"));
    }

    private static SessionCommandAliases CreateAliases() => new("0.14.0.98");

    private static void Define(SessionCommandAliases aliases, string definition)
    {
        Assert.True(aliases.TryDefine(definition, out _, out _, out var error));
        Assert.Null(error);
    }
}
