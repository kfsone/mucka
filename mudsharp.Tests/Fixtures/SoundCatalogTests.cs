using Mucka.Audio;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The sound catalogue is what the Sounds settings tab is built from - one collapsible group per
/// entry, each leaf getting an enable checkbox, a preview button and a volume slider
/// (FkeyEditorViewModel builds it generically by walking <see cref="SoundCatalog.Groups"/>). So a
/// sound that is missing from the catalogue is a sound the player cannot turn down or switch off,
/// and one whose asset path is wrong is a settings row that plays silence.
///
/// <para>Both of those are easy to cause by accident and invisible until someone opens the settings
/// dialog and clicks the right preview button. The file-existence check below is the one that earns
/// its keep: renaming a wav without updating the catalogue entry compiles, ships, and fails only in
/// the player's ears.</para>
/// </summary>
public class SoundCatalogTests
{
    /// <summary>Walks up from the test binary to the repo root (the directory holding Mucka.csproj).
    /// Returns null when it cannot be found, so a checkout-shaped assumption degrades into a skip
    /// rather than a spurious failure - the same courtesy CombatCaptureReplayTests extends to its
    /// capture file.</summary>
    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        return dir;
    }

    [Fact]
    public void EveryCataloguedSound_HasAFileOnDisk()
    {
        var root = FindRepoRoot();
        if (root is null)
            return;   // not a source checkout; nothing to verify against

        var missing = new List<string>();
        foreach (var group in SoundCatalog.Groups)
        {
            foreach (var sound in group.Sounds)
            {
                // AssetName is package-relative ("sounds/x.wav"); on disk that is Resources/Raw/.
                var path = Path.Combine(root.FullName, "Resources", "Raw",
                    sound.AssetName.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    missing.Add($"{group.Name}/{sound.Name} -> {sound.AssetName}");
            }
        }

        Assert.True(missing.Count == 0,
            "Catalogued sounds with no file shipped (the settings row exists but plays silence):\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>Codes are the persisted settings key - a duplicate would mean two rows sharing one
    /// enable flag and one volume, so toggling either silently moves the other.</summary>
    [Fact]
    public void SoundCodes_AreUnique()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in SoundCatalog.Groups)
        {
            foreach (var sound in group.Sounds)
            {
                Assert.False(seen.ContainsKey(sound.Code),
                    $"Code '{sound.Code}' is used by both '{seen.GetValueOrDefault(sound.Code)}' "
                    + $"and '{group.Name}/{sound.Name}'.");
                seen[sound.Code] = $"{group.Name}/{sound.Name}";
            }
        }
    }

    /// <summary>Group prefixes key the group's own enable flag and volume, so they too must be
    /// distinct.</summary>
    [Fact]
    public void GroupPrefixes_AreUnique()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in SoundCatalog.Groups)
            Assert.True(seen.Add(group.Prefix), $"Duplicate group prefix '{group.Prefix}'.");
    }

    /// <summary>
    /// The failed-flee buzzer specifically: it must be catalogued, because that is the only thing
    /// that gives it a volume slider and an off switch, and an alert the player cannot turn down is a
    /// misfeature. Pinned by asset path as well as code, since the two have already drifted apart
    /// once during a rename.
    /// </summary>
    [Fact]
    public void FleeFailedAlert_IsCatalogued_SoItHasAVolumeControl()
    {
        var hit = SoundCatalog.FindByAsset("sounds/mucka.flee_failed.wav");
        Assert.NotNull(hit);
        Assert.Equal("alert-flee-failed", hit!.Value.Def.Code);
        // No fallback picker for this family: there is no numeric code family behind it for a
        // fallback to stand in for (same shape as the tell alerts).
        Assert.False(hit.Value.Group.HasFallback);
    }
}
