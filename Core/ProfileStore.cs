using Microsoft.Maui.Storage;

namespace Mucka.Core;

/// <summary>
/// SecureStorage-backed password store for connection profiles. The profiles themselves
/// live in mucka.ini - see SettingsStore.LoadProfilesAsync/SaveProfilesAsync.
/// </summary>
public static class ProfileStore
{
    public static async Task<string?> GetPasswordAsync(string profileName)
    {
        try
        {
#if MACCATALYST
            return await Task.Run(() => MacKeychain.Get($"pwd:{profileName}")).ConfigureAwait(false);
#else
            return await SecureStorage.GetAsync($"pwd:{profileName}").ConfigureAwait(false);
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] SecureStorage.GetAsync failed for '{profileName}': {ex}");
            throw;
        }
    }

    public static async Task SetPasswordAsync(string profileName, string? password)
    {
        var key = $"pwd:{profileName}";
#if MACCATALYST
        // SecureStorage needs a keychain entitlement an ad-hoc signed build cannot carry; see MacKeychain.
        if (string.IsNullOrEmpty(password))
            await Task.Run(() => MacKeychain.Remove(key)).ConfigureAwait(false);
        else
            await Task.Run(() => MacKeychain.Set(key, password)).ConfigureAwait(false);
#else
        if (string.IsNullOrEmpty(password))
            SecureStorage.Remove(key);
        else
            await SecureStorage.SetAsync(key, password).ConfigureAwait(false);
#endif
    }
}
