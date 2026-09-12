using Mucka.Combat;
using Mucka.WireLog;

namespace Mucka.Core;

/// <summary>
/// The two on-disk locations <see cref="ClogWriter"/> and its siblings write into. Split out of
/// ClogWriter itself (which used to own these) so that class can stay free of MAUI references and
/// remain linkable into mudsharp.Tests, the same reasoning CombatDb/FightHistoryStore/SwingLedger
/// already follow for their own directories - only this one file needs to compile against
/// <c>FileSystem.Current</c> (Microsoft.Maui.Storage), which a plain xunit project cannot resolve.
/// </summary>
internal static class ClogPaths
{
    /// <summary>~/.mucka/clogs (desktop) - shared by encounter clogs and the "$eval" item-stats log
    /// (items.jsonl), so both live side by side.</summary>
    internal static string GetClogDirectory()
    {
        // Desktop: literally ~/.mucka/clogs, matching the ~/.mucka/mapping and ~/.mucka/combat
        // convention used for this project's other stores.
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "clogs");

        // Mobile: no home-directory concept - use the platform cache directory instead,
        // same rationale as SessionCapture.GetCaptureDirectory.
        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "clogs");
    }

    /// <summary>Where the combat database lives - ~/.mucka/combat on desktop, matching the ~/.mucka
    /// store convention, and the platform cache directory on mobile for the same reason
    /// <see cref="GetClogDirectory"/> uses it.</summary>
    internal static string GetCombatDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "combat");

        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "combat");
    }

    /// <summary>Where the wire-log database lives - ~/.mucka/wire on desktop, alongside the other
    /// ~/.mucka stores, and the platform cache directory on mobile for the same reason the two above
    /// use it. Its own directory as well as its own file: see <see cref="WireLogDb"/> for why it is
    /// kept apart from the combat database.</summary>
    internal static string GetWireLogDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "wire");

        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "wire");
    }

    /// <summary>Where the manual JSONL session recordings go. Desktop capture files are transient
    /// debug artifacts, so temp rather than roaming app data.</summary>
    internal static string GetCaptureDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), "mucka");

        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka");
    }
}
