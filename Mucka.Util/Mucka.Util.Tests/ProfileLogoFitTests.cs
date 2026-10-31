using Mucka.Ui;

namespace Mucka.Util.Tests;

public class ProfileLogoFitTests
{
    private const double Width = 300.0;

    [Fact]
    public void RoomForBoth_ShowsTheWholeLogo()
    {
        var fit = ProfileLogoFit.Compute(Width, ProfileLogoFit.ListNeedDp + Width + 50, UiFontScale.Normal, ProfileLogoFit.ListNeedDp);
        Assert.True(fit.Show);
        Assert.Equal(Width, fit.LogoHeight);
        Assert.Equal(0, fit.Overlap);
        Assert.Equal(Width, fit.SpaceTaken);
    }

    [Fact]
    public void ShortOfRoom_TheListCoversTheLogosBottom()
    {
        var fit = ProfileLogoFit.Compute(Width, ProfileLogoFit.ListNeedDp + Width - 60, UiFontScale.Normal, ProfileLogoFit.ListNeedDp);
        Assert.True(fit.Show);
        Assert.Equal(60, fit.Overlap, 6);
        Assert.Equal(Width - 60, fit.SpaceTaken, 6);
    }

    [Fact]
    public void TheOverlapStopsAtTheBottomHalf()
    {
        var fit = ProfileLogoFit.Compute(Width, ProfileLogoFit.ListNeedDp + Width / 2, UiFontScale.Normal, ProfileLogoFit.ListNeedDp);
        Assert.True(fit.Show);
        Assert.Equal(Width / 2, fit.Overlap, 6);
    }

    [Fact]
    public void PastTheBottomHalf_TheLogoIsLeftOut()
    {
        var fit = ProfileLogoFit.Compute(Width, ProfileLogoFit.ListNeedDp + Width / 2 - 1, UiFontScale.Normal, ProfileLogoFit.ListNeedDp);
        Assert.False(fit.Show);
        Assert.Equal(0, fit.SpaceTaken);
    }

    [Theory]
    [InlineData(0.0, 700.0)]
    [InlineData(300.0, 0.0)]
    [InlineData(-1.0, 700.0)]
    public void ANotYetSizedPane_ShowsNoLogo(double width, double height)
        => Assert.False(ProfileLogoFit.Compute(width, height, UiFontScale.Normal, ProfileLogoFit.ListNeedDp).Show);

    [Fact]
    public void ALargerStep_NeedsMoreRoomForTheList()
    {
        var height = ProfileLogoFit.ListNeedDp + Width;
        Assert.Equal(0, ProfileLogoFit.Compute(Width, height, UiFontScale.Normal, ProfileLogoFit.ListNeedDp).Overlap);
        Assert.True(ProfileLogoFit.Compute(Width, height, 2, ProfileLogoFit.ListNeedDp).Overlap > 0);
    }

    [Fact]
    public void TheDenseNeed_LeavesTheLogoMoreRoom()
    {
        var height = ProfileLogoFit.DenseListNeedDp + Width;
        Assert.Equal(0, ProfileLogoFit.Compute(Width, height, UiFontScale.Normal, ProfileLogoFit.DenseListNeedDp).Overlap);
        Assert.True(ProfileLogoFit.Compute(Width, height, UiFontScale.Normal, ProfileLogoFit.ListNeedDp).Overlap > 0);
    }

    [Fact]
    public void ASmallerStep_NeedsNoLessThanTheDefault()
        => Assert.Equal(
            ProfileLogoFit.Compute(Width, 500, UiFontScale.Normal, ProfileLogoFit.ListNeedDp),
            ProfileLogoFit.Compute(Width, 500, UiFontScale.Min, ProfileLogoFit.ListNeedDp));
}
