using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Mucka.Core;

public static class ProfileStore
{
    private static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "profiles.json");

    // Serializes writers: concurrent saves shared one tmp file, so overlapping writes threw
    // IOException (swallowed upstream — the settings dialog's silent save failure).
    private static readonly SemaphoreSlim s_writeGate = new(1, 1);

    public static async Task<List<Profile>> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return Defaults();
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<Profile>>(json) ?? Defaults();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Defaults();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] failed to load profiles: {ex}");
            throw;
        }
    }

    public static async Task SaveAsync(List<Profile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles,
            new JsonSerializerOptions { WriteIndented = true });
        await s_writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tmpPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
            // File.Move has no async overload; this is an atomic metadata-only rename on the same volume.
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        finally
        {
            s_writeGate.Release();
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

    private static List<Profile> Defaults() => new()
    {
        new Profile { Name = "MUD2 UK",  Host = "mud2.co.uk",  Port = 23    },
        new Profile { Name = "MUD2.COM", Host = "www.mud2.com", Port = 27723 },
    };
}
