using Mucka.WireLog;

namespace Mucka.Core;

/// <summary>One replay History's Replays tab lists.</summary>
/// <param name="Name">The file name without <c>.muckrep</c>: what <c>replay:</c> takes.</param>
/// <param name="Path">The file on disk.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Modified">Its last write, local time.</param>
/// <param name="Packaged">Shipped with the app: it can be played and shared, not renamed or
/// deleted.</param>
internal sealed record ReplayEntry(string Name, string Path, long Bytes, DateTime Modified, bool Packaged);

/// <summary>
/// The replays <see cref="ReplayFiles"/> can play, listed in the order it searches them, so a name
/// held by two places is listed once, as the one that plays.
///
/// <para>Packaged replays are read from the app's own folder (Windows) or bundle resources (Mac);
/// <c>FileSystem.OpenAppPackageFileAsync</c> opens one by name but cannot list them. A build that puts
/// them elsewhere lists none, and they still play by name.</para>
/// </summary>
internal static class ReplayLibrary
{
    /// <summary>Synchronous file listing; callers run it off the UI thread.</summary>
    public static List<ReplayEntry> List()
    {
        var byName = new Dictionary<string, ReplayEntry>(StringComparer.OrdinalIgnoreCase);
        Add(byName, MuckaPaths.GetReplayDirectory(), packaged: false);
#if ANDROID
        if (Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath is { } external)
            Add(byName, external, packaged: false);
#endif
        if (PackagedDirectory() is { } packagedDirectory)
            Add(byName, packagedDirectory, packaged: true);
        return [.. byName.Values.OrderByDescending(e => e.Modified)];
    }

    private static string? PackagedDirectory()
    {
#if WINDOWS
        return System.IO.Path.Combine(AppContext.BaseDirectory, "replays");
#elif MACCATALYST
        return Foundation.NSBundle.MainBundle.ResourcePath is { } resources
            ? System.IO.Path.Combine(resources, "replays")
            : null;
#else
        return null;
#endif
    }

    private static void Add(Dictionary<string, ReplayEntry> into, string directory, bool packaged)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory, "*" + Muckrep.Extension))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (into.ContainsKey(name))
                continue;
            var info = new FileInfo(path);
            into[name] = new ReplayEntry(name, path, info.Length, info.LastWriteTime, packaged);
        }
    }

    /// <summary>The name a player typed, trimmed and without a <c>.muckrep</c> they added themselves:
    /// the extension is the file's, and <c>replay:</c> takes the name without it.</summary>
    public static string NameFromTyped(string typed)
    {
        var name = typed.Trim();
        return name.EndsWith(Muckrep.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^Muckrep.Extension.Length].TrimEnd()
            : name;
    }

    /// <summary>Why <paramref name="name"/> cannot be a replay's new name, or null when it can.
    /// <paramref name="current"/> is the replay being renamed; <paramref name="listed"/> is every
    /// replay <see cref="List"/> found. A name another listed replay holds is refused whichever folder
    /// it is in, because only one of the two would list and play.</summary>
    public static string? RenameProblem(ReplayEntry current, string name, IEnumerable<ReplayEntry> listed)
    {
        if (name.Length == 0)
            return "A replay needs a name.";
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0)
            return "That name has characters a file name cannot hold.";
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current.Path)!, name + Muckrep.Extension);
        var taken = listed.Any(e => !string.Equals(e.Path, current.Path, StringComparison.Ordinal)
                                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (taken || (!string.Equals(target, current.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)))
            return $"There is already a replay called \"{name}\".";
        return null;
    }

    /// <summary>Renames the file in its own folder. Check <see cref="RenameProblem"/> first.</summary>
    public static void Rename(ReplayEntry current, string name)
    {
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current.Path)!, name + Muckrep.Extension);
        File.Move(current.Path, target);
    }
}
