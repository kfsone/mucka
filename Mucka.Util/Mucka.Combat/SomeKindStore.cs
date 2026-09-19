using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Carries <see cref="SomeKindKnowledge"/> between runs through the <c>some_kinds</c> table: one row
/// per species, upserted on every reveal. The knowledge itself belongs to the session's combat
/// tracker; this only reads the table into it at startup and writes each reveal back.
///
/// <para>Threading, the same contract as <see cref="FightHistoryStore"/>: <see cref="LoadAsync"/> is
/// fire-and-forget from startup and does its SQLite work on the thread pool; the reveal handler runs
/// on the Feed thread and only enqueues a row, so the write happens on <see cref="MuckaStore"/>'s
/// single background task (Invariant #1). Rows restored by the load raise no reveal, so a load never
/// writes back what it just read.</para>
/// </summary>
public sealed class SomeKindStore
{
    private readonly MuckaStore _store;
    private readonly SomeKindKnowledge _knowledge;
    private readonly Action<string, Exception>? _onError;

    public SomeKindStore(MuckaStore store, SomeKindKnowledge knowledge, Action<string, Exception>? onError = null)
    {
        _store = store;
        _knowledge = knowledge;
        _onError = onError;
        _knowledge.Revealed += OnRevealed;
    }

    private void OnRevealed(string species, SomeKind kind)
        => _store.Enqueue(new SomeKindRow(species, SomeKinds.Word(kind), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    /// <summary>Reads every row into the knowledge. Call once at startup, OFF the UI thread. A kind
    /// learned live before this completes is kept over the stored one - see
    /// <see cref="SomeKindKnowledge.Restore"/>.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);
        foreach (var (species, kind) in rows)
            _knowledge.Restore(species, kind);
    }

    private List<(string Species, SomeKind Kind)> LoadCore()
    {
        var rows = new List<(string, SomeKind)>();
        try
        {
            using var connection = MuckaDb.Open(_store.Path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT species, kind FROM some_kinds;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), SomeKinds.FromWord(reader.GetString(1))));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Best-effort: an unreadable table costs attribution, never the game.
            _onError?.Invoke("SomeKindStore.Load", ex);
        }
        return rows;
    }
}

internal sealed record SomeKindRow(string Species, string Kind, long LearnedMs) : IStoreRow
{
    public void Write(StoreWrite write)
    {
        var command = write.Prepared(
            "INSERT INTO some_kinds (species, kind, learned_ms) VALUES ($species, $kind, $learned) " +
            "ON CONFLICT(species) DO UPDATE SET kind = excluded.kind, learned_ms = excluded.learned_ms;");
        command.Parameters.AddWithValue("$species", Species);
        command.Parameters.AddWithValue("$kind", Kind);
        command.Parameters.AddWithValue("$learned", LearnedMs);
        command.ExecuteNonQuery();
    }
}
