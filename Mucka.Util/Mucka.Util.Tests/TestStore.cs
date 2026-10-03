using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// How a test opens and closes a <see cref="MuckaStore"/>: so a store that faults says so, instead of
/// quietly recording nothing.
///
/// <para>The store reports every failure only through its optional <c>onError</c> callback and never
/// throws, so a test that passes none - as the store's production callers mostly do not need to - meets
/// a failed open or a faulted writer later, as missing rows or a missing table in whatever it reads
/// back. Under a loaded full-solution run that surfaced as failures in unrelated tests, never in
/// isolation: <c>StaminaReadLedgerTests.OnlyAKillCeilingsThePool</c> reading the wrong pool, and
/// <c>ClogWriterTests</c> finding no <c>encounter_stats</c> table.</para>
///
/// <para>Tests that exercise the store's own fault reporting pass their own <c>onError</c> and do not
/// use this.</para>
/// </summary>
internal static class TestStore
{
    private static readonly ConditionalWeakTable<MuckaStore, List<string>> Faults = new();

    /// <summary>Opens a store, failing the test at once - with the store's own exception - if the open
    /// faulted.</summary>
    public static MuckaStore Open(string path, string host = "test", string? clientVersion = null)
    {
        var faults = new List<string>();
        Exception? first = null;
        var store = new MuckaStore(path, host, clientVersion, (context, ex) =>
        {
            lock (faults)
            {
                first ??= ex;
                faults.Add(context + " :: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
        if (store.IsFaulted)
            throw new InvalidOperationException(
                $"MuckaStore faulted opening {path}: {string.Join(" | ", faults)}", first);
        Faults.AddOrUpdate(store, faults);
        return store;
    }

    /// <summary>
    /// Releases the pooled connections to every file under <paramref name="directory"/>, so the
    /// directory can be deleted. Only those pools: <c>SqliteConnection.ClearAllPools</c> clears every
    /// pool in the process, and xunit runs test classes in parallel.
    ///
    /// <para>EVIDENCE: with every test class calling <c>ClearAllPools</c> in its cleanup, a loaded run
    /// failed in <c>MuckaDb.Open</c> with <c>ObjectDisposedException</c> on the native
    /// <c>SQLitePCL.sqlite3</c> handle, mid-PRAGMA, on a connection another class had just opened -
    /// the store came up faulted and the test read back nothing.</para>
    /// </summary>
    public static void ReleasePools(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            using (var writable = new SqliteConnection(MuckaDb.ConnectionString(file)))
                SqliteConnection.ClearPool(writable);
            using (var readOnly = new SqliteConnection(MuckaDb.ReadConnectionString(file)))
                SqliteConnection.ClearPool(readOnly);
        }
    }

    /// <summary>Disposes the store, which drains everything queued to disk, and fails the test if the
    /// writer faulted at any point - a faulted writer discards what it held.</summary>
    public static void Close(MuckaStore store)
    {
        store.Dispose();
        if (!Faults.TryGetValue(store, out var faults))
            return;
        lock (faults)
        {
            if (faults.Count > 0)
                throw new InvalidOperationException(
                    "MuckaStore faulted before it closed: " + string.Join(" | ", faults));
        }
    }
}
