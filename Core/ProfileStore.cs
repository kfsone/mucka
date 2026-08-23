using Microsoft.Maui.Storage;

namespace Mucka.Core;

/// <summary>
/// SecureStorage-backed password store for connection profiles. The profiles themselves
/// live in mucka.ini — see SettingsStore.LoadProfilesAsync/SaveProfilesAsync.
/// </summary>
public static class ProfileStore
{
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
