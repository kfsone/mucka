using Mucka.Ui;

namespace Mucka.Util.Tests;

public class UiFontScaleTests
{
    [Theory]
    [InlineData(8.0)]
    [InlineData(11.0)]
    [InlineData(13.5)]
    [InlineData(28.0)]
    public void Normal_ReturnsTheAuthoredSizeUntouched(double size)
    {
        Assert.Equal(size, UiFontScale.Apply(size, UiFontScale.Normal));
        Assert.Equal(size, UiFontScale.Apply(size, UiFontScale.Normal, tight: true));
    }

    [Fact]
    public void Steps_AreStrictlyIncreasing()
    {
        for (var step = UiFontScale.Min; step < UiFontScale.Max; step++)
            Assert.True(UiFontScale.ScaleFor(step) < UiFontScale.ScaleFor(step + 1), $"step {step}");
    }

    [Fact]
    public void PlusFour_UndoesTheMacShrink()
        => Assert.InRange(UiFontScale.ScaleFor(4) * 0.77, 0.99, 1.03);

    [Theory]
    [InlineData(11.0, 2, 12.5)]
    [InlineData(11.0, 4, 14.5)]
    [InlineData(14.0, 2, 16.0)]
    [InlineData(14.0, 4, 18.5)]
    [InlineData(11.0, -5, 8.0)]
    public void Scaled_RoundsToTheNearestHalfPoint(double size, int step, double expected)
        => Assert.Equal(expected, UiFontScale.Apply(size, step));

    [Fact]
    public void Tight_StopsAtPlusFour()
    {
        Assert.Equal(UiFontScale.Apply(13.0, 4), UiFontScale.Apply(13.0, UiFontScale.Max, tight: true));
        Assert.True(UiFontScale.Apply(13.0, UiFontScale.Max) > UiFontScale.Apply(13.0, UiFontScale.Max, tight: true));
        Assert.Equal(UiFontScale.Apply(13.0, 3), UiFontScale.Apply(13.0, 3, tight: true));
    }

    [Theory]
    [InlineData(-11, -8)]
    [InlineData(-8, -8)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(11, 8)]
    public void Clamp_KeepsTheStepInRange(int step, int expected)
        => Assert.Equal(expected, UiFontScale.Clamp(step));

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData("", false, 0)]
    [InlineData("junk", false, 0)]
    [InlineData("1", true, 1)]
    [InlineData("-3", true, -3)]
    [InlineData("10", true, 8)]
    [InlineData("-10", true, -8)]
    public void TryParse_ClampsANumber_AndRefusesAnythingElse(string? text, bool parsed, int expected)
    {
        Assert.Equal(parsed, UiFontScale.TryParse(text, out var step));
        Assert.Equal(expected, step);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(0)]
    [InlineData(8)]
    public void Format_RoundTripsThroughTryParse(int step)
    {
        Assert.True(UiFontScale.TryParse(UiFontScale.Format(step), out var parsed));
        Assert.Equal(step, parsed);
    }

    [Theory]
    [InlineData(-5, "-5")]
    [InlineData(0, "Default")]
    [InlineData(3, "+3")]
    [InlineData(11, "+8")]
    public void Name_IsDefaultOrTheSignedStep(int step, string expected)
        => Assert.Equal(expected, UiFontScale.Name(step));

    [Fact]
    public void PanelScale_GrowsAboveNormalAndNeverNarrows()
    {
        Assert.Equal(1.0, UiFontScale.PanelScale(UiFontScale.Min));
        Assert.Equal(1.0, UiFontScale.PanelScale(UiFontScale.Normal));
        Assert.Equal(UiFontScale.ScaleFor(3), UiFontScale.PanelScale(3));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "2")]
    [InlineData("2", "4")]
    [InlineData("junk", "0")]
    public void MigrateLegacyKey_TranslatesAndRemovesTheOldKey(string legacy, string expected)
    {
        var ini = new Dictionary<string, string> { [UiFontScale.LegacyIniKey] = legacy };
        Assert.True(Migrate(ini));
        Assert.Equal(expected, ini[UiFontScale.IniKey]);
        Assert.False(ini.ContainsKey(UiFontScale.LegacyIniKey));
    }

    [Fact]
    public void MigrateLegacyKey_KeepsANewValue()
    {
        var ini = new Dictionary<string, string> { [UiFontScale.LegacyIniKey] = "2", [UiFontScale.IniKey] = "-1" };
        Assert.True(Migrate(ini));
        Assert.Equal("-1", ini[UiFontScale.IniKey]);
        Assert.False(ini.ContainsKey(UiFontScale.LegacyIniKey));
    }

    [Fact]
    public void MigrateLegacyKey_NothingToDo()
    {
        var ini = new Dictionary<string, string> { [UiFontScale.IniKey] = "3" };
        Assert.False(Migrate(ini));
        Assert.Equal("3", ini[UiFontScale.IniKey]);
    }

    private static bool Migrate(Dictionary<string, string> ini)
        => UiFontScale.MigrateLegacyKey(
            (_, k) => ini.TryGetValue(k, out var v) ? v : null,
            (_, k, v) => ini[k] = v,
            (_, k) => ini.Remove(k));
}
