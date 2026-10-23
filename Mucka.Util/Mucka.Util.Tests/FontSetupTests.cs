using Mucka.Ui;

namespace Mucka.Util.Tests;

/// <summary>docs/font-setup-design.md: err on the side of oversized.</summary>
public class FontSetupTests
{
    [Fact]
    public void FreshInstall_OnTheMac_IsAboveTheBaselineAndTheDefaultFont()
    {
        var c = FontSetup.Initial(null, null, answered: false, isMac: true);
        Assert.True(c.UiStep > FontSetup.MacBaselineStep);
        Assert.True(c.GameFontPx > FontSetup.DefaultGameFontPx);
    }

    [Fact]
    public void FreshInstall_Elsewhere_IsAboveNormal()
    {
        var c = FontSetup.Initial(null, null, answered: false, isMac: false);
        Assert.True(c.UiStep > UiFontScale.Normal);
        Assert.Equal(FontSetup.DefaultGameFontPx, c.GameFontPx);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(2)]
    public void Unanswered_OnTheMac_NeverOpensBelowTheBaseline(int stored)
        => Assert.Equal(FontSetup.MacBaselineStep, FontSetup.Initial(stored, 15, answered: false, isMac: true).UiStep);

    [Fact]
    public void Unanswered_OnTheMac_KeepsALargerStep()
        => Assert.Equal(5, FontSetup.Initial(5, 15, answered: false, isMac: true).UiStep);

    [Fact]
    public void Unanswered_Elsewhere_KeepsTheirOwnValues()
    {
        var c = FontSetup.Initial(-2, 13, answered: false, isMac: false);
        Assert.Equal(new FontSetupChoice(-2, 13), c);
    }

    [Fact]
    public void Unanswered_WithOnlyAGameFontStored_IsNotFresh()
        => Assert.Equal(UiFontScale.Normal, FontSetup.Initial(null, 16, answered: false, isMac: false).UiStep);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Answered_IsUntouched(bool isMac)
        => Assert.Equal(new FontSetupChoice(-1, 12), FontSetup.Initial(-1, 12, answered: true, isMac));

    [Fact]
    public void GameFont_IsClamped()
    {
        Assert.Equal(FontSetup.MaxGameFontPx, FontSetup.Initial(0, 99, answered: true, isMac: false).GameFontPx);
        Assert.Equal(FontSetup.MinGameFontPx, FontSetup.Initial(0, 3, answered: true, isMac: false).GameFontPx);
    }

    [Theory]
    [InlineData(FontSetup.DefaultGameFontPx, "Default")]
    [InlineData(FontSetup.DefaultGameFontPx + 5, "+5")]
    [InlineData(FontSetup.DefaultGameFontPx - 2, "-2")]
    [InlineData(99, "+9")]
    public void GameFontName_IsDefaultOrTheSignedOffset(int px, string expected)
        => Assert.Equal(expected, FontSetup.GameFontName(px));

    [Theory]
    [InlineData(null, true)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    public void ShouldPrompt_OnlyWhenNeverAnswered(string? done, bool expected)
        => Assert.Equal(expected, FontSetup.ShouldPrompt(done));
}
