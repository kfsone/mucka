using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

public sealed class PlayerNamePartsTests
{
    [Theory]
    [InlineData("Ollie the mage", false, "", "Ollie", " the mage")]
    [InlineData("(Ollie the mage)", true, "", "Ollie", " the mage")]
    [InlineData("Sir Ollie", false, "Sir ", "Ollie", "")]
    [InlineData("Lady Polly", false, "Lady ", "Polly", "")]
    [InlineData("(Lady Polly)", true, "Lady ", "Polly", "")]
    public void Parse_SeparatesPersonaFromTitleAndDescription(
        string input,
        bool invisible,
        string title,
        string persona,
        string description)
    {
        var result = PlayerNameParts.Parse(input);

        Assert.Equal(invisible, result.IsInvisible);
        Assert.Equal(title, result.TitlePrefix);
        Assert.Equal(persona, result.PersonaName);
        Assert.Equal(description, result.DescriptionSuffix);
    }

    // The who-list renders these three spans back to back, so concatenating them must reproduce
    // the server's own text exactly -- no paren gained, lost, or duplicated.
    [Theory]
    [InlineData("Ollie the warlock")]
    [InlineData("(Ollie the warlock)")]
    [InlineData("Sir Ollie the knight")]
    [InlineData("(Sir Ollie the knight)")]
    [InlineData("Ollie")]
    [InlineData("(Ollie)")]
    public void DisplayParts_ConcatenateBackToTheWireName(string wire)
    {
        var (prefix, name, suffix) = PlayerNameParts.Parse(wire).DisplayParts(namesOnly: false);

        Assert.Equal(wire, prefix + name + suffix);
    }

    [Theory]
    [InlineData("(Ollie the warlock)", "(", "Ollie", " the warlock)")]
    [InlineData("(Sir Ollie the knight)", "(Sir ", "Ollie", " the knight)")]
    [InlineData("Ollie the warlock", "", "Ollie", " the warlock")]
    public void DisplayParts_KeepInvisibilityParensOutOfTheNameSpan(
        string wire, string prefix, string name, string suffix)
    {
        // The name span is rendered 2pt larger than its neighbours, so a paren that leaks into it
        // is visibly wrong -- and an untitled invisible player used to get "((Ollie the warlock)"
        // because the prefix and name spans both emitted the opening paren.
        var parts = PlayerNameParts.Parse(wire).DisplayParts(namesOnly: false);

        Assert.Equal(prefix, parts.Prefix);
        Assert.Equal(name, parts.Name);
        Assert.Equal(suffix, parts.Suffix);
        Assert.DoesNotContain('(', parts.Name);
        Assert.DoesNotContain(')', parts.Name);
    }

    // The "is this line about my persona?" rule, shared by the self-chat colouring and the
    // spoken-dreamword cancellation. Invisibility parenthesises the whole name-and-description,
    // which the dreamword check used to miss entirely.
    [Theory]
    [InlineData("Ollie says \"x\".", true)]
    [InlineData("Ollie the necromancer says \"x\".", true)]
    [InlineData("(Ollie the warlock) says \"x\".", true)]
    [InlineData("(Ollie) says \"x\".", true)]
    [InlineData("Ollie waves.", true)]
    // Boundary: a longer name that merely starts with ours is a different persona, parens or not.
    [InlineData("Ollier says \"x\".", false)]
    [InlineData("(Ollier the warlock) says \"x\".", false)]
    // Someone else entirely, and the degenerate cases.
    [InlineData("Someone says \"x\".", false)]
    [InlineData("Ollie", false)]
    [InlineData("", false)]
    public void StartsWithPersona_TolerantOfInvisibilityParens(string text, bool expected)
        => Assert.Equal(expected, PlayerNameParts.StartsWithPersona(text, "Ollie"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void StartsWithPersona_FalseWithoutAName(string? persona)
        => Assert.False(PlayerNameParts.StartsWithPersona("Ollie says \"x\".", persona));

    [Theory]
    [InlineData("(Ollie the warlock)", "(Ollie)")]
    [InlineData("(Sir Ollie the knight)", "(Ollie)")]
    [InlineData("Ollie the warlock", "Ollie")]
    public void DisplayParts_NamesOnlyPutsEverythingInTheNameSpan(string wire, string expected)
    {
        // Names-only mode drops the small spans entirely, so the name span has to carry the
        // invisibility parens itself -- there is nothing else left to render them.
        var (prefix, name, suffix) = PlayerNameParts.Parse(wire).DisplayParts(namesOnly: true);

        Assert.Equal(string.Empty, prefix);
        Assert.Equal(expected, name);
        Assert.Equal(string.Empty, suffix);
    }
}
