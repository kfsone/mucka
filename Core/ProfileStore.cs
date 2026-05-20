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
            var json = await File.ReadAllTextAsync(FilePath);
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
        await File.WriteAllTextAsync(FilePath, json);
    }

    private static List<Profile> Defaults() => new()
    {
        new Profile { Name = "MUD2 UK", Host = "mud2.co.uk", Port = 23 },
    };
}
