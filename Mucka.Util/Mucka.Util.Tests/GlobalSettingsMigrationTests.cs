using Mucka.Settings;

namespace Mucka.Util.Tests;

public class GlobalSettingsMigrationTests
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

    [Fact]
    public async Task AbsentGlobalAdoptsFromMostRecentlyUsedProfileFirst()
    {
        var ini = await Parse("""
            [profiles]
            1=B
            2=A

            [settings:A]
            volume=10
            [settings:B]
            volume=90
            """);
        Assert.True(GlobalSettingsMigration.Apply(ini));
        Assert.Equal("90", ini.Get("settings", "volume"));
    }

    [Fact]
    public async Task PresentGlobalIsNeverOverwritten()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [settings]
            volume=40
            [settings:A]
            volume=90
            """);
        GlobalSettingsMigration.Apply(ini);
        Assert.Equal("40", ini.Get("settings", "volume"));
    }

    [Fact]
    public async Task ProfileSectionKeysMoveAndAreRemoved()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [profile:A]
            host=h
            columns=100
            antiidle=60
            keepscreenon=yes
            defaulthotkeys=no
            """);
        Assert.True(GlobalSettingsMigration.Apply(ini));
        Assert.Equal("100", ini.Get("settings", "columns"));
        Assert.Equal("60", ini.Get("settings", "antiidle"));
        Assert.Equal("yes", ini.Get("settings", "keepscreenon"));
        Assert.Equal("no", ini.Get("settings", "defaulthotkeys"));
        Assert.Equal("h", ini.Get("profile:A", "host"));
        foreach (var key in GlobalSettingsMigration.ProfileSectionGlobalKeys)
            Assert.Null(ini.Get("profile:A", key));
    }

    [Fact]
    public async Task SoundFamilyMovesAsAUnitFromTheFirstSourceWithSoundKeys()
    {
        var ini = await Parse("""
            [profiles]
            1=A
            2=B

            [settings:A]
            volume=10
            [settings:B]
            sounds=yes
            soundgroup.07=off
            soundvol.0703=50
            """);
        GlobalSettingsMigration.Apply(ini);
        Assert.Equal("yes", ini.Get("settings", "sounds"));
        Assert.Equal("off", ini.Get("settings", "soundgroup.07"));
        Assert.Equal("50", ini.Get("settings", "soundvol.0703"));
        Assert.False(ini.HasSection("settings:A"));
        Assert.False(ini.HasSection("settings:B"));
    }

    [Fact]
    public async Task ExistingGlobalSoundKeysBlockAdoption()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [settings]
            sounds=yes
            [settings:A]
            sounds=no
            soundgroup.07=off
            """);
        GlobalSettingsMigration.Apply(ini);
        Assert.Equal("yes", ini.Get("settings", "sounds"));
        Assert.Null(ini.Get("settings", "soundgroup.07"));
    }

    [Fact]
    public async Task UnknownKeysAndOtherSectionsSurvive()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [profile:A]
            host=h
            password=secret
            futurekey=1
            antiidle=5

            [settings:A]
            volume=10
            futureoverride=x

            [fkeys:A]
            F1=l

            [watch]
            rule=1
            """);
        GlobalSettingsMigration.Apply(ini);
        Assert.Equal("secret", ini.Get("profile:A", "password"));
        Assert.Equal("1", ini.Get("profile:A", "futurekey"));
        Assert.Equal("x", ini.Get("settings:A", "futureoverride"));
        Assert.Null(ini.Get("settings:A", "volume"));
        Assert.Equal("l", ini.Get("fkeys:A", "F1"));
        Assert.Equal("1", ini.Get("watch", "rule"));
    }

    [Fact]
    public async Task OrphanedOverrideSectionIsAdoptedLastThenDropped()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [profile:A]
            host=h

            [settings:Gone]
            fontsize=20
            """);
        GlobalSettingsMigration.Apply(ini);
        Assert.Equal("20", ini.Get("settings", "fontsize"));
        Assert.False(ini.HasSection("settings:Gone"));
    }

    [Fact]
    public async Task SecondRunChangesNothing()
    {
        var ini = await Parse("""
            [profiles]
            1=A

            [profile:A]
            columns=100
            [settings:A]
            volume=10
            """);
        Assert.True(GlobalSettingsMigration.Apply(ini));
        Assert.False(GlobalSettingsMigration.Apply(ini));
    }

    [Fact]
    public async Task CleanFileIsLeftAlone()
    {
        var ini = await Parse("[settings]\nvolume=5\n\n[profile:A]\nhost=h\n");
        Assert.False(GlobalSettingsMigration.Apply(ini));
    }

    [Fact]
    public async Task ProfileNamesAreMruThenUnlisted()
    {
        var ini = await Parse("[profiles]\n1=B\n\n[profile:A]\nhost=h\n\n[profile:B]\nhost=h\n");
        Assert.Equal(["B", "A"], GlobalSettingsMigration.ProfileNames(ini));
    }
}
