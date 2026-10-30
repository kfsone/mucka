using Mucka.Settings;

namespace Mucka.Util.Tests;

public class ProfileRenameTests
{
    private static async Task<IniFile> Parse(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mucka-ini-{Guid.NewGuid():N}.ini");
        try
        {
            await File.WriteAllTextAsync(path, text);
            return IniFile.Load(path);
        }
        finally { File.Delete(path); }
    }

    private const string TwoProfiles = """
        [profiles]
        1=Main
        2=Alt

        [profile:Main]
        host=mud2.co.uk
        loginname=mud
        password=abc

        [fkeys:Main]
        f1=look

        [profile:Alt]
        host=mud2.com
        """;

    [Fact]
    public async Task EverySectionAndTheOrderEntryMove()
    {
        var ini = await Parse(TwoProfiles);
        Assert.True(ProfileRename.Apply(ini, "Main", "Home"));

        Assert.False(ini.HasSection("profile:Main"));
        Assert.False(ini.HasSection("fkeys:Main"));
        Assert.Equal("mud2.co.uk", ini.Get("profile:Home", "host"));
        Assert.Equal("mud", ini.Get("profile:Home", "loginname"));
        Assert.Equal("abc", ini.Get("profile:Home", "password"));
        Assert.Equal("look", ini.Get("fkeys:Home", "f1"));
        Assert.Equal("Home", ini.Get("profiles", "1"));
        Assert.Equal("Alt", ini.Get("profiles", "2"));
    }

    [Fact]
    public async Task OtherProfilesAreUntouched()
    {
        var ini = await Parse(TwoProfiles);
        ProfileRename.Apply(ini, "Main", "Home");
        Assert.Equal("mud2.com", ini.Get("profile:Alt", "host"));
    }

    [Fact]
    public async Task ANameAnotherProfileHasIsRefused()
    {
        var ini = await Parse(TwoProfiles);
        Assert.False(ProfileRename.Apply(ini, "Main", "alt"));
        Assert.Equal("mud2.co.uk", ini.Get("profile:Main", "host"));
        Assert.Equal("Main", ini.Get("profiles", "1"));
    }

    [Fact]
    public async Task ChangingOnlyTheCaseIsARename()
    {
        var ini = await Parse(TwoProfiles);
        Assert.True(ProfileRename.Apply(ini, "Main", "MAIN"));
        Assert.Equal("mud2.co.uk", ini.Get("profile:MAIN", "host"));
        Assert.Equal("MAIN", ini.Get("profiles", "1"));
    }

    [Theory]
    [InlineData("", "Home")]
    [InlineData("Main", " ")]
    public async Task ABlankNameIsRefused(string oldName, string newName)
    {
        var ini = await Parse(TwoProfiles);
        Assert.False(ProfileRename.Apply(ini, oldName, newName));
        Assert.Equal("mud2.co.uk", ini.Get("profile:Main", "host"));
    }
}
