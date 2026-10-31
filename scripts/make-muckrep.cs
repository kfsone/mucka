#:project ../Mucka.Util/Mucka.WireLog/Mucka.WireLog.csproj
// Make a replay (.muckrep) from a run in a mucka.db - see docs/replay.md.
//
// Run from the repo root. With only a database, lists its runs:
//   dotnet run --file scripts/make-muckrep.cs -- ~/.mucka/mucka.db
// With a run, writes the replay - optionally only records first..last (the run's seq, inclusive),
// and with any number of edits, each replacing every case-blind OLD with NEW:
//   dotnet run --file scripts/make-muckrep.cs -- <mucka.db> <run id> <out.muckrep> [first] [last] [--sub OLD=NEW]...
//
// The account ID and password become Muckrep.PlaceholderAccountId and Muckrep.PlaceholderPassword
// wherever the server echoed them (Muckrep.FromSession); nothing the client sent is written at all.

using System.Globalization;
using Mucka.WireLog;

var edits = new List<Muckrep.Substitution>();
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--sub" && i + 1 < args.Length && args[i + 1].IndexOf('=') > 0)
    {
        var parts = args[++i].Split('=', 2);
        edits.Add(new Muckrep.Substitution(parts[0], parts[1]));
    }
    else
        positional.Add(args[i]);
}

if (positional.Count == 1)
{
    foreach (var run in WireLogExport.ListSessions(positional[0]))
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{run.Id,6}  {DateTimeOffset.FromUnixTimeMilliseconds(run.StartedMs).LocalDateTime:yyyy-MM-dd HH:mm}  {run.Records,7} records  {run.Host}"));
    return 0;
}
if (positional.Count is < 3 or > 5)
{
    Console.Error.WriteLine("usage: make-muckrep <mucka.db> [<run id> <out.muckrep> [first] [last] [--sub OLD=NEW]...]");
    return 2;
}

var db = positional[0];
var runId = long.Parse(positional[1], CultureInfo.InvariantCulture);
var outPath = positional[2];
var first = positional.Count > 3 ? int.Parse(positional[3], CultureInfo.InvariantCulture) : 0;
var last = positional.Count > 4 ? int.Parse(positional[4], CultureInfo.InvariantCulture) : int.MaxValue;

var session = WireLogExport.ReadSession(db, runId).ToList();
if (session.Count == 0)
{
    Console.Error.WriteLine($"run {runId} has no records in {db}");
    return 1;
}
var replay = Muckrep.FromSession(session, first, last, edits: edits);
if (replay.Count == 0)
{
    Console.Error.WriteLine($"records {first}..{last} of run {runId} hold nothing the server sent");
    return 1;
}
using (var file = File.Create(outPath))
    Muckrep.Write(file, ["muckrep", "account ID and password replaced with placeholders"], replay);
Console.WriteLine($"{outPath}: {replay.Count} records, {replay[^1].TimestampMs / 1000.0:0.0}s");
return 0;
