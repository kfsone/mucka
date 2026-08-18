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
        // Desktop: literally ~/.mucka/clogs, matching the offline research tooling's
        // ~/.mucka/mapping and ~/.mucka/combat convention (tools/mapping, tools/combat).
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "clogs");

        // Mobile: no home-directory concept - use the platform cache directory instead,
        // same rationale as SessionCapture.GetCaptureDirectory.
        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "clogs");
    }

    /// <summary>Where the combat database lives - ~/.mucka/combat on desktop, matching the offline
    /// tooling's own convention (tools/combat writes its reduced combat.db into the same directory),
    /// and the platform cache directory on mobile for the same reason
    /// <see cref="GetClogDirectory"/> uses it.</summary>
    internal static string GetCombatDirectory()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "combat");

        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka", "combat");
    }
}
