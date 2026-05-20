using MudSharp.Models;
using MudSharp.Transport;

var host = args.Length > 0 ? args[0] : "mud2.co.uk";
var port = args.Length > 1 ? int.Parse(args[1]) : 23;

await using var conn = new TcpMudConnection();

conn.LineReady       += RenderLine;
conn.StatsUpdated    += _ => { };
conn.GameModeEntered += () => Console.WriteLine("[Game mode]");
conn.GameModeExited  += () => Console.WriteLine("[Disconnected]");
conn.Disconnected    += ex =>
    Console.WriteLine(ex != null ? $"[Connection lost: {ex.Message}]" : "[Connection closed]");

Console.WriteLine($"Connecting to {host}:{port}...");
await conn.ConnectAsync(host, port);
Console.WriteLine("Connected. CTRL-T=stats  CTRL-W=dreamword  ESC/CTRL-C=quit");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var inputBuffer = new System.Text.StringBuilder();

while (!cts.IsCancellationRequested)
{
    ConsoleKeyInfo key;
    try { key = Console.ReadKey(intercept: true); }
    catch (InvalidOperationException) { break; } // stdin redirected

    if (key.Key == ConsoleKey.Escape ||
       (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
    {
        cts.Cancel();
        break;
    }

    if (key.Key == ConsoleKey.T && key.Modifiers.HasFlag(ConsoleModifiers.Control))
    {
        Console.WriteLine();
        PrintStats(conn.Session.CurrentStats);
        // Redraw the current input buffer
        Console.Write(inputBuffer);
        continue;
    }

    if (key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control))
    {
        Console.WriteLine();
        var dw = conn.Session.CurrentDreamword;
        if (dw != null) { conn.SendLine(dw); Console.WriteLine($"[Sent dreamword: {dw}]"); }
        else Console.WriteLine("[No dreamword known]");
        Console.Write(inputBuffer);
        continue;
    }

    if (key.Key == ConsoleKey.Enter)
    {
        var line = inputBuffer.ToString();
        inputBuffer.Clear();
        Console.WriteLine();
        conn.SendLine(line);
        continue;
    }

    if (key.Key == ConsoleKey.Backspace)
    {
        if (inputBuffer.Length > 0)
        {
            inputBuffer.Remove(inputBuffer.Length - 1, 1);
            Console.Write("\b \b");
        }
        continue;
    }

    if (!char.IsControl(key.KeyChar))
    {
        inputBuffer.Append(key.KeyChar);
        Console.Write(key.KeyChar);
    }
}

await conn.DisconnectAsync();

// ── Helpers ────────────────────────────────────────────────────────────────────

void RenderLine(StyledLine line)
{
    foreach (var span in line.Spans)
    {
        var style = span.Style;
        if (style.Foreground != AnsiColor.Default)
            Console.ForegroundColor = ToConsoleColor(style.Foreground);
        if (style.Background != AnsiColor.Default)
            Console.BackgroundColor = ToConsoleColor(style.Background);
        Console.Write(span.Text);
        Console.ResetColor();
    }
    if (!line.IsPartial) Console.WriteLine();
}

void PrintStats(GameStatsSnapshot s)
{
    Console.WriteLine(
        $"[Stats] Sta:{s.Stamina}/{s.MaxStamina}  Score:{s.Score}  " +
        $"Str:{s.Strength}  Dex:{s.Dexterity}  TTR:{s.TimeToReset}s  " +
        $"DW:{s.DreamWord ?? "none"}  Saved:{s.PersonaSaved}");
}

static ConsoleColor ToConsoleColor(AnsiColor color) => color switch
{
    AnsiColor.Black         => ConsoleColor.Black,
    AnsiColor.Red           => ConsoleColor.DarkRed,
    AnsiColor.Green         => ConsoleColor.DarkGreen,
    AnsiColor.Yellow        => ConsoleColor.DarkYellow,
    AnsiColor.Blue          => ConsoleColor.DarkBlue,
    AnsiColor.Magenta       => ConsoleColor.DarkMagenta,
    AnsiColor.Cyan          => ConsoleColor.DarkCyan,
    AnsiColor.White         => ConsoleColor.Gray,
    AnsiColor.BrightBlack   => ConsoleColor.DarkGray,
    AnsiColor.BrightRed     => ConsoleColor.Red,
    AnsiColor.BrightGreen   => ConsoleColor.Green,
    AnsiColor.BrightYellow  => ConsoleColor.Yellow,
    AnsiColor.BrightBlue    => ConsoleColor.Blue,
    AnsiColor.BrightMagenta => ConsoleColor.Magenta,
    AnsiColor.BrightCyan    => ConsoleColor.Cyan,
    AnsiColor.BrightWhite   => ConsoleColor.White,
    _                       => ConsoleColor.Gray,
};
