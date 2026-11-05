namespace Mucka.Store;

/// <summary>How eager the client is to back the database up before a risky change - the player's
/// choice, kept in <c>mucka.ini</c>. A change that can lose nothing never backs up at all
/// (<see cref="MigrationScripts.IsAdditive"/>); this decides how often the rest do.</summary>
public enum BackupPolicy
{
    /// <summary>Every launch that is about to make a risky change backs up first.</summary>
    Startup,

    /// <summary>Not when the last backup is less than an hour old.</summary>
    Hourly,

    /// <summary>Not when the last backup is less than a day old.</summary>
    Daily,

    /// <summary>Not when the last backup is less than a week old.</summary>
    Weekly,

    /// <summary>Never.</summary>
    Never,
}

/// <summary>The ini spelling of a <see cref="BackupPolicy"/>, and the age its backup must have reached
/// before another is taken.</summary>
public static class BackupPolicies
{
    /// <summary>What an absent or unreadable setting means: the policy that always backs up before a
    /// risky change.</summary>
    public const BackupPolicy Default = BackupPolicy.Startup;

    /// <summary>The ini value for <paramref name="policy"/>.</summary>
    public static string Format(BackupPolicy policy) => policy switch
    {
        BackupPolicy.Startup => "startup",
        BackupPolicy.Hourly => "hourly",
        BackupPolicy.Daily => "daily",
        BackupPolicy.Weekly => "weekly",
        BackupPolicy.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    /// <summary>The policy an ini value names, case and surrounding space ignored; <see cref="Default"/>
    /// for null or anything else.</summary>
    public static BackupPolicy Parse(string? value)
    {
        foreach (var policy in Enum.GetValues<BackupPolicy>())
            if (string.Equals(Format(policy), value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return policy;
        return Default;
    }

    /// <summary>How old the existing backup must be before another is taken: none for a policy that
    /// always backs up, null for the one that never does.</summary>
    public static TimeSpan? MinimumAge(BackupPolicy policy) => policy switch
    {
        BackupPolicy.Startup => TimeSpan.Zero,
        BackupPolicy.Hourly => TimeSpan.FromHours(1),
        BackupPolicy.Daily => TimeSpan.FromDays(1),
        BackupPolicy.Weekly => TimeSpan.FromDays(7),
        BackupPolicy.Never => null,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
}
