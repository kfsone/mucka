using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// One unit of work for <see cref="MuckaStore"/>'s writer: almost always a single INSERT, occasionally
/// a parent row plus its children, occasionally an UPDATE.
///
/// <para>The seam is deliberately at the STATEMENT and not at the row shape. Row building stays with
/// the class that understands the data - the swing ledger still builds swing rows, the clog writer
/// still builds encounter rows - and only the connection and the transaction move to one owner.</para>
///
/// <para><b>A row must be fully materialised before it is enqueued.</b> <see cref="Write"/> runs on the
/// writer task, not on the thread that created the row, so it may not read anything that the producer
/// thread can still be mutating.</para>
/// </summary>
public interface IStoreRow
{
    /// <summary>Writes this row inside the current transaction. Runs on the store's single writer
    /// task.</summary>
    void Write(StoreWrite write);
}

/// <summary>
/// What a row gets to write itself with: prepared statements, cached for the store's lifetime and
/// re-pointed at the current transaction, plus the rowid the last statement assigned (for a child
/// row that has to reference its parent).
/// </summary>
public sealed class StoreWrite
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, SqliteCommand> _prepared = new(StringComparer.Ordinal);
    private SqliteTransaction? _transaction;

    internal StoreWrite(SqliteConnection connection) => _connection = connection;

    internal void Begin(SqliteTransaction transaction)
    {
        _transaction = transaction;
        foreach (var command in _prepared.Values)
            command.Transaction = transaction;
    }

    internal void DisposeCommands()
    {
        foreach (var command in _prepared.Values)
            command.Dispose();
        _prepared.Clear();
    }

    /// <summary>The command for <paramref name="sql"/>, with its parameters cleared and ready to bind.
    /// Cached: the same statement text always returns the same command object, so SQLite prepares each
    /// statement once for the life of the store.</summary>
    public SqliteCommand Prepared(string sql)
    {
        if (!_prepared.TryGetValue(sql, out var command))
        {
            command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = _transaction;
            _prepared[sql] = command;
        }
        command.Parameters.Clear();
        return command;
    }

    /// <summary>The rowid SQLite assigned to the row most recently inserted on this connection. Only
    /// meaningful immediately after an INSERT in the same <see cref="IStoreRow.Write"/> call.</summary>
    public long LastInsertRowId()
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>SQL NULL for a missing value. Never 0 - see the schema's note on fabricated
    /// measurements.</summary>
    public static object Value(object? value) => value ?? DBNull.Value;
}
