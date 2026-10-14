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
            // Not asked yet reads as nothing stored, and never touches the keychain.
            return await SettingsStore.GetSecurePasswordsAsync().ConfigureAwait(false) switch
            {
                // A password the keychain refused to take at migration is still in the ini.
                true  => await Task.Run(() => MacKeychain.Get($"pwd:{profileName}")).ConfigureAwait(false)
                         ?? await SettingsStore.GetPlainPasswordAsync(profileName).ConfigureAwait(false),
                false => await SettingsStore.GetPlainPasswordAsync(profileName).ConfigureAwait(false),
                null  => null,
            };
#else
            return await SecureStorage.GetAsync($"pwd:{profileName}").ConfigureAwait(false);
#endif
        }
        catch (Exception ex)
        {
            CrashLog.Warn("ProfileStore", $"password lookup failed for '{profileName}': {ex}");
            throw;
        }
    }

    public static async Task SetPasswordAsync(string profileName, string? password)
    {
        var key = $"pwd:{profileName}";
#if MACCATALYST
        // SecureStorage needs a keychain entitlement an ad-hoc signed build cannot carry; see MacKeychain.
        var secure = await SettingsStore.GetSecurePasswordsAsync().ConfigureAwait(false);
        if (secure is null)
        {
            // Nothing is written anywhere before the user has chosen where passwords go.
            if (string.IsNullOrEmpty(password)) return;
            throw new InvalidOperationException("Password storage has not been chosen yet.");
        }
        if (secure == false)
            await SettingsStore.SetPlainPasswordAsync(profileName, password).ConfigureAwait(false);
        else
        {
            if (string.IsNullOrEmpty(password))
                await Task.Run(() => MacKeychain.Remove(key)).ConfigureAwait(false);
            else
                await Task.Run(() => MacKeychain.Set(key, password)).ConfigureAwait(false);
            // The keychain now holds the truth; a copy left in the ini by a refused migration goes.
            await SettingsStore.SetPlainPasswordAsync(profileName, null).ConfigureAwait(false);
        }
#else
        if (string.IsNullOrEmpty(password))
            SecureStorage.Remove(key);
        else
            await SecureStorage.SetAsync(key, password).ConfigureAwait(false);
#endif
    }

#if MACCATALYST
    /// <summary>
    /// Records where passwords are kept from now on and moves the saved ones there. A password is
    /// written to the new place before it is removed from the old, and one that cannot be moved
    /// (the keychain refused it) stays where it was. <paramref name="moveFromOld"/> false records the
    /// choice only: the first choice made with "plain text" must not read the keychain, which would
    /// raise the prompt the choice exists to avoid.
    /// </summary>
    public static async Task ChooseSecureStorageAsync(bool secure, bool moveFromOld)
    {
        await s_chooseGate.WaitAsync().ConfigureAwait(false);
        try { await ChooseCoreAsync(secure, moveFromOld).ConfigureAwait(false); }
        finally { s_chooseGate.Release(); }
    }

    private static readonly SemaphoreSlim s_chooseGate = new(1, 1);

    private static async Task ChooseCoreAsync(bool secure, bool moveFromOld)
    {
        await SettingsStore.SetSecurePasswordsAsync(secure).ConfigureAwait(false);
        if (!moveFromOld) return;

        if (secure)
        {
            // Every profile is checked, not only those remembering a password: none may be left in the
            // file. Each password is in the keychain before it is queued for removal, and the file is
            // written once at the end.
            var migrated = new List<string>();
            var all = (await SettingsStore.LoadProfilesAsync().ConfigureAwait(false)).Select(p => p.Name).ToList();
            await Mucka.Commands.PasswordMigration.MoveAsync(all,
                SettingsStore.GetPlainPasswordAsync,
                (n, pw) => Task.Run(() => MacKeychain.Set($"pwd:{n}", pw)),
                n => { migrated.Add(n); return Task.CompletedTask; }).ConfigureAwait(false);
            await SettingsStore.RemovePlainPasswordsAsync(migrated).ConfigureAwait(false);
        }
        else
        {
            var names = (await SettingsStore.LoadProfilesAsync().ConfigureAwait(false))
                .Where(p => p.RememberPassword).Select(p => p.Name).ToList();
            await Mucka.Commands.PasswordMigration.MoveAsync(names,
                n => Task.Run(() => MacKeychain.Get($"pwd:{n}")),
                SettingsStore.SetPlainPasswordAsync,
                n => Task.Run(() => MacKeychain.Remove($"pwd:{n}"))).ConfigureAwait(false);
        }
    }
#endif
}
