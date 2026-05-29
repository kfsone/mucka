using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Mucka.Core;

public static class ProfileStore
{
    private static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "profiles.json");

    public static async Task<List<Profile>> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return Defaults();
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<Profile>>(json) ?? Defaults();
        }
        catch
        {
            return Defaults();
        }
    }

    public static async Task SaveAsync(List<Profile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
    }

    public static async Task<string?> GetPasswordAsync(string profileName)
    {
        try { return await SecureStorage.GetAsync($"pwd:{profileName}").ConfigureAwait(false); }
        catch { return null; }
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
