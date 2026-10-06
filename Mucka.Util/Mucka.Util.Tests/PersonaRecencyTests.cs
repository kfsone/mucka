using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>The persona picker's most-recently-played-first ordering, read from persona_sessions.</summary>
public sealed class PersonaRecencyTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-recency-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static void Login(MuckaStore store, long startedMs, string host, string persona)
    {
        var id = store.BeginPersonaSession(startedMs, host)
            ?? throw new InvalidOperationException("BeginPersonaSession returned no id");
        store.NamePersonaSession(id, persona);
    }

    [Fact]
    public void LastStarted_is_the_latest_login_per_persona_on_that_host_only()
    {
        using (var store = new MuckaStore(DbPath, "mud2.co.uk"))
        {
            Login(store, 100, "mud2.co.uk", "Alpha");
            Login(store, 300, "mud2.co.uk", "Alpha");
            Login(store, 200, "mud2.co.uk", "Beta");
            Login(store, 900, "other.example", "Beta");
            var unnamed = store.BeginPersonaSession(950, "mud2.co.uk");
            Assert.NotNull(unnamed);
        }

        var last = PersonaRecency.LastStarted(DbPath, "mud2.co.uk");

        Assert.Equal(2, last.Count);
        Assert.Equal(300, last["Alpha"]);
        Assert.Equal(200, last["BETA"]);
    }

    [Fact]
    public void A_missing_database_reads_as_no_history()
        => Assert.Empty(PersonaRecency.LastStarted(Path.Combine(_directory, "absent.db"), "h"));

    [Fact]
    public void Most_recent_first_and_never_played_keep_slot_order_at_the_end()
    {
        var last = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Beta"] = 200,
            ["Delta"] = 500,
        };

        var ordered = PersonaRecency.OrderMostRecentFirst(
            ["Alpha", "Beta", "Gamma", "Delta"], n => n, last);

        Assert.Equal(["Delta", "Beta", "Alpha", "Gamma"], ordered);
    }
}
