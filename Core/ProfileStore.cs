using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Mucka.Core;

/// <summary>
/// SecureStorage-backed password store, plus the legacy profiles.json reader that feeds
/// SettingsStore's one-time migration. The profiles themselves now live in mucka.ini —
/// see SettingsStore.LoadProfilesAsync/SaveProfilesAsync.
/// </summary>
public static class ProfileStore
{
    private static string LegacyPath =>
        Path.Combine(FileSystem.AppDataDirectory, "profiles.json");

    /// <summary>
    /// Reads the legacy profiles.json, or null when it is absent or unreadable —
    /// the migration treats any failure as "no legacy file".
    /// </summary>
    public static async Task<List<Profile>?> TryLoadLegacyAsync()
    {
        try
        {
            if (!File.Exists(LegacyPath)) return null;
            var json = await File.ReadAllTextAsync(LegacyPath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<Profile>>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] legacy profiles.json unreadable, skipping migration: {ex}");
            return null;
        }
    }

    /// <summary>Renames the migrated profiles.json to profiles.unused so it is not re-imported.</summary>
    public static void RetireLegacyFile()
    {
        try
        {
            File.Move(LegacyPath, Path.ChangeExtension(LegacyPath, ".unused"), overwrite: true);
        }
        catch (Exception ex)
        {
            // The migration already saved the profiles into the ini; a lingering json is
            // harmless (the ini now defines profiles, so it won't be imported again).
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] could not retire profiles.json: {ex}");
        }
    }

    public static async Task<string?> GetPasswordAsync(string profileName)
    {
        try { return await SecureStorage.GetAsync($"pwd:{profileName}").ConfigureAwait(false); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] SecureStorage.GetAsync failed for '{profileName}': {ex}");
            throw;
        }
    }

    public static async Task SetPasswordAsync(string profileName, string? password)
    {
        var key = $"pwd:{profileName}";
        if (string.IsNullOrEmpty(password))
            SecureStorage.Remove(key);
        else
            await SecureStorage.SetAsync(key, password).ConfigureAwait(false);
    }
}
