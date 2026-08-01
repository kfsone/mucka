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
}
