using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>The policy is process-wide, so these run with nothing else running (a test that backs up
/// would otherwise see another test's choice).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackupPolicyRunsAlone
{
    public const string Name = "BackupPolicy";
}

/// <summary>How eager the client is to back up (<see cref="BackupPolicy"/>): the ini spelling, the age
/// the last backup must have reached, and that a schema upgrade honours it.</summary>
[Collection(BackupPolicyRunsAlone.Name)]
public sealed class BackupPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-backup-policy-tests", Guid.NewGuid().ToString("N"));
    private readonly BackupPolicy _before = DatabaseBackup.Policy;

    public BackupPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        DatabaseBackup.Policy = _before;
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private string BackupPath => DatabaseBackup.PathFor(DbPath);

    // A current file with one backup already beside it, the backup `age` old.
    private void SeedWithABackupOfAge(TimeSpan age)
    {
        using (MuckaDb.Open(DbPath)) { }
        DatabaseBackup.Take(DbPath);
        File.SetLastWriteTimeUtc(BackupPath, DateTime.UtcNow - age);
        Assert.False(DatabaseBackup.HoldsBackup(DbPath));
    }

    [Theory]
    [InlineData(BackupPolicy.Startup, "startup")]
    [InlineData(BackupPolicy.Hourly, "hourly")]
    [InlineData(BackupPolicy.Daily, "daily")]
    [InlineData(BackupPolicy.Weekly, "weekly")]
    [InlineData(BackupPolicy.Never, "never")]
    public void EveryPolicy_RoundTripsThroughItsIniSpelling(BackupPolicy policy, string spelling)
    {
        Assert.Equal(spelling, BackupPolicies.Format(policy));
        Assert.Equal(policy, BackupPolicies.Parse(spelling));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("often")]
    public void AnAbsentOrUnknownValue_MeansTheOneThatAlwaysBacksUp(string? value)
        => Assert.Equal(BackupPolicy.Startup, BackupPolicies.Parse(value));

    [Fact]
    public void TheSpelling_IgnoresCaseAndSpaces()
        => Assert.Equal(BackupPolicy.Weekly, BackupPolicies.Parse("  Weekly "));

    [Fact]
    public void Never_TakesNoBackup()
    {
        using (MuckaDb.Open(DbPath)) { }
        DatabaseBackup.Policy = BackupPolicy.Never;

        Assert.False(DatabaseBackup.IsDue(DbPath));
        Assert.Null(DatabaseBackup.TakeOnce(DbPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Startup_TakesOneEvenWhenTheLastWasJustMade()
    {
        SeedWithABackupOfAge(TimeSpan.Zero);
        DatabaseBackup.Policy = BackupPolicy.Startup;

        Assert.NotNull(DatabaseBackup.TakeOnce(DbPath));
    }

    [Theory]
    [InlineData(BackupPolicy.Hourly, 30, false)]
    [InlineData(BackupPolicy.Hourly, 90, true)]
    [InlineData(BackupPolicy.Daily, 23 * 60, false)]
    [InlineData(BackupPolicy.Daily, 25 * 60, true)]
    [InlineData(BackupPolicy.Weekly, 6 * 24 * 60, false)]
    [InlineData(BackupPolicy.Weekly, 8 * 24 * 60, true)]
    public void ABackupIsTakenOnlyOnceTheLastIsOldEnough(BackupPolicy policy, int ageMinutes, bool due)
    {
        SeedWithABackupOfAge(TimeSpan.FromMinutes(ageMinutes));
        DatabaseBackup.Policy = policy;

        Assert.Equal(due, DatabaseBackup.IsDue(DbPath));
        Assert.Equal(due, DatabaseBackup.TakeOnce(DbPath) is not null);
    }

    [Theory]
    [InlineData(BackupPolicy.Hourly)]
    [InlineData(BackupPolicy.Daily)]
    [InlineData(BackupPolicy.Weekly)]
    public void WithNoBackupYet_EveryPolicyButNeverTakesOne(BackupPolicy policy)
    {
        using (MuckaDb.Open(DbPath)) { }
        DatabaseBackup.Policy = policy;

        Assert.True(DatabaseBackup.IsDue(DbPath));
    }

    // A file as an older release left it: the baseline's tables, nothing journaled - an upgrade that
    // is not additive.
    private void WriteAnUnjournaledFile()
    {
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = MigrationScripts.All[0].Contents;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void ASchemaUpgrade_HonoursNever()
    {
        WriteAnUnjournaledFile();
        DatabaseBackup.Policy = BackupPolicy.Never;

        using (MuckaDb.Open(DbPath)) { }

        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void ASchemaUpgrade_SkipsTheBackupWhenTheLastIsYoungerThanTheAge()
    {
        WriteAnUnjournaledFile();
        File.WriteAllText(BackupPath, "a backup from a minute ago");
        DatabaseBackup.Policy = BackupPolicy.Daily;

        using (MuckaDb.Open(DbPath)) { }

        Assert.Equal("a backup from a minute ago", File.ReadAllText(BackupPath));
    }

    [Fact]
    public void ASchemaUpgrade_BacksUpWhenTheLastIsOlderThanTheAge()
    {
        WriteAnUnjournaledFile();
        File.WriteAllText(BackupPath, "a backup from last week");
        File.SetLastWriteTimeUtc(BackupPath, DateTime.UtcNow.AddDays(-2));
        DatabaseBackup.Policy = BackupPolicy.Daily;

        using (MuckaDb.Open(DbPath)) { }

        Assert.NotEqual("a backup from last week", File.ReadAllText(BackupPath));
    }
}
