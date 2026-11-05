using System.Text;
using Mucka.Combat;
using Mucka.Store;
using Mucka.WireLog;
using MudSharp.Protocol;

namespace Mucka.Util.Tests;

/// <summary>
/// The live writer and the heal state one rule, in two places that cannot share the event wiring: the
/// connection (MAUI) hands each read to the writer and then to the parser, and closes or abandons the
/// unit on the parser's events; <see cref="WireHeal"/> does the same over stored rows. This drives the
/// writer exactly as the connection does, and the heal over the same reads written raw, and requires the
/// same rows.
/// </summary>
public sealed class WireJoinAgreementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-wirejoin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly byte[] Enter = [0x9D, 0x9C, 0xFF, 0xFF];

    private static readonly byte[] Prompt = [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, 0xFF, 0xFF];

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private readonly record struct Event(bool Tx, byte[] Data);

    private static Event Rx(byte[] data) => new(false, data);

    private static Event Rx(string text) => new(false, Latin1(text));

    private static Event Tx(string text) => new(true, Latin1(text));

    // A session as the wire carries it: a message split around a command, a prompt split across two
    // reads, a heartbeat's naked prompt, a login, the option menu, and a second login.
    private static List<Event> Session() =>
    [
        Rx("Account ID:"), Rx(Enter), Rx("Welcome.\r\n"), Rx(Prompt),
        Rx("You hit"), Rx(" the wyvern.\r\n"), Tx("look\r\n"), Rx(Cat(Latin1("It dies.\r\n"), Prompt[..5])), Rx(Prompt[5..]),
        Rx("plain "), Rx("text\r\n"), Rx(Prompt),
        Rx(Prompt),
        Rx("A single read.\r\n"), Rx(Prompt),
        Rx("half a "), Rx("line, then\r\n"),
        Rx("Option (H for help)"),
        Rx("Account ID:"), Rx("kfsone"), Rx(Enter), Rx("Back "), Rx("again.\r\n"), Tx("score\r\n"), Rx(Prompt),
    ];

    private string Record(string name, bool coalesce)
    {
        var path = Path.Combine(_dir, name, MuckaDb.DefaultFileName);
        var store = TestStore.Open(path);
        using (var writer = new WireLogWriter(store) { CoalesceUnits = coalesce })
        {
            var parser = new MudStreamParser();
            parser.PromptClosed += writer.CloseUnit;
            parser.GameModeEntered += writer.AbandonUnit;
            parser.GameModeExited += writer.AbandonUnit;
            foreach (var e in Session())
            {
                if (e.Tx)
                {
                    writer.RecordTx(e.Data);
                    continue;
                }
                writer.RecordRx(e.Data);
                parser.Feed(e.Data);
            }
        }
        TestStore.Close(store);
        return path;
    }

    private static List<(long Seq, int Direction, string Data)> Rows(string path)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, direction, data FROM wire ORDER BY id;";
        using var reader = command.ExecuteReader();
        var rows = new List<(long, int, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt32(1), Encoding.Latin1.GetString((byte[])reader.GetValue(2))));
        return rows;
    }

    [Fact]
    public void TheLiveWriter_AndTheHeal_LeaveTheSameRows()
    {
        var live = Record("live", coalesce: true);
        var raw = Record("raw", coalesce: false);
        var rawRows = Rows(raw);

        var result = WireHeal.Run(raw);

        var healed = Rows(raw);
        Assert.Equal(1, result.RunsHealed);
        Assert.True(healed.Count < rawRows.Count);
        Assert.Equal(Rows(live), healed);
        // Pinned, so two sides that both failed to join the split-prompt unit would not agree on nothing.
        Assert.Contains(healed, r => r.Direction == 0
            && r.Data == "You hit the wyvern.\r\nIt dies.\r\n" + Encoding.Latin1.GetString(Prompt));
        // The reads open when the session ended were not joined into the next login's.
        Assert.Contains(healed, r => r.Data == "half a ");
        Assert.Contains(healed, r => r.Data == "line, then\r\n");
        Assert.Contains(healed, r => r.Direction == 0 && r.Data == "Back again.\r\n" + Encoding.Latin1.GetString(Prompt));
    }
}
