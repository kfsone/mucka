namespace Mucka.Settings;

/// <summary>
/// Renames a connection profile inside mucka.ini: its identity section, its per-profile F-key
/// section and its entry in the MRU order all move to the new name, keys and values unchanged.
/// </summary>
public static class ProfileRename
{
    public const string FkeysSectionPrefix = "fkeys:";

    /// <summary>Moves <paramref name="oldName"/>'s sections to <paramref name="newName"/>. False, with
    /// nothing changed, when either name is blank or the new name already has a section of its own.</summary>
    public static bool Apply(IniFile ini, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return false;
        string[] prefixes = [GlobalSettingsMigration.ProfileSectionPrefix, FkeysSectionPrefix];
        // A section can differ from the old one's only by case and still be the same profile.
        var sameProfile = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!sameProfile && prefixes.Any(prefix => ini.HasSection(prefix + newName)))
            return false;

        foreach (var prefix in prefixes)
        {
            var from = prefix + oldName;
            if (!ini.HasSection(from))
                continue;
            var items = ini.Items(from).ToList();
            ini.RemoveSection(from);
            var to = prefix + newName;
            ini.EnsureSection(to);
            foreach (var (key, value) in items)
                ini.Set(to, key, value);
        }

        foreach (var (key, value) in ini.Items(GlobalSettingsMigration.ProfilesSection).ToList())
            if (string.Equals(value, oldName, StringComparison.OrdinalIgnoreCase))
                ini.Set(GlobalSettingsMigration.ProfilesSection, key, newName);
        return true;
    }
}
