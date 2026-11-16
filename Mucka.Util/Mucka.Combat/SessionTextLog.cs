using Mucka.Terminal;
using Mucka.WireLog;
using MudSharp.Models;
using MudSharp.Session;

namespace Mucka.Combat;

/// <summary>What an ANSI log is drawn with: the player's chat colours and faces, the persona whose
/// own messages they mark (<see cref="ChatColorizer.IsSelf"/>), and how many colours the log may
/// use.</summary>
public sealed record LogColours(ChatColorizer.Palette Palette, string? Persona, SgrColours Colours);

/// <summary>
/// A Session's text log: the screen as plain text - or, as an ANSI log, with each span's colour and
/// face as ANSI SGR (<see cref="SgrText"/>), or as a web page (<see cref="HtmlText"/>) - written by
/// the live transcript's own
/// <see cref="SessionRecorder"/>, made afterwards from the Session's recording
/// (<see cref="SessionSlice"/>) instead of from the socket.
///
/// <para><b>The persona session only.</b> The log runs from the persona entering the game to it
/// leaving, as the parser itself detects them (<see cref="MudSession.GameModeEntered"/>,
/// <see cref="MudSession.GameModeExited"/>): the first entry inside the slice - or the slice's start,
/// when the persona is already in the game there - to the exit after it, or the slice's end when the
/// recording stops first. The login, the character menu and the option
/// menu after the game are the slice's, and not the Session's play.</para>
///
/// <para>The run is fed from its first record into one session, as live: a run is one connection,
/// and the connection had one <see cref="MudSession"/>. So the parser's state at the entry is what
/// it was live. Each tx record goes to <see cref="MudSession.NoteRecordedSend"/> in its place, so the setup
/// batch's and the value probes' echoes and replies are hidden exactly where a live send hid them.
/// Probe replies (FES/FEW/FEI/FEX) never become lines at all. As live, the line in progress is
/// emitted after each record read outside game mode (<c>MuckaConnection.ReadLoopAsync</c>).</para>
///
/// <para>The account ID is masked as a replay's is (<see cref="Muckrep.MaskLogin"/>). The client's
/// own system lines and F-key notes are not in the recording, so they are not in the log.</para>
///
/// <para>The Session viewer starts from the same feed's lines (<see cref="Lines"/>).</para>
///
/// <para>Synchronous parsing, then the recorder's own write; off the UI thread.</para>
/// </summary>
public static class SessionTextLog
{
    /// <summary>Writes the log to <paramref name="writer"/>, which it closes, whatever happens.</summary>
    /// <param name="colours">For an ANSI log: the chat colours and faces to draw with, the persona
    /// whose own messages they mark, and the colour depth. Null writes plain text.</param>
    /// <exception cref="IOException">The write failed.</exception>
    /// <exception cref="InvalidOperationException">The recording never enters the game.</exception>
    public static Task WriteAsync(SessionSlice slice, string host, TextWriter writer,
        LogColours? colours = null)
        => WriteAsync(slice, host, writer,
            colours is { } c ? line => SgrText.Format(line, c.Palette, c.Colours) : null, colours?.Persona);

    /// <summary>Writes the log as a web page (<see cref="HtmlText"/>) to <paramref name="writer"/>,
    /// which it closes, whatever happens: the same lines as the ANSI log, in the pane's colours and the
    /// player's chat colours, the persona's own messages marked as <paramref name="persona"/>'s.</summary>
    /// <param name="title">The page's title.</param>
    /// <exception cref="IOException">The write failed.</exception>
    /// <exception cref="InvalidOperationException">The recording never enters the game.</exception>
    public static Task WriteHtmlAsync(SessionSlice slice, string host, TextWriter writer,
        ChatColorizer.Palette palette, string? persona, string title)
    {
        ArgumentNullException.ThrowIfNull(writer);
        HtmlPageWriter page;
        try
        {
            page = new HtmlPageWriter(writer, title);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        // The recorder writes the host into its first and last lines as it is.
        return WriteAsync(slice, HtmlText.Encode(host), page, line => HtmlText.Format(line, palette), persona);
    }

    /// <param name="format">How a line is written; plain text when null. A formatted log's lines are
    /// held and marked over the whole Session before they are written.</param>
    private static async Task WriteAsync(SessionSlice slice, string host, TextWriter writer,
        Func<StyledLine, string>? format, string? persona)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var play = new Play(holds: format is not null,
            ms => new SessionRecorder(writer, host, Local(ms), format));
        try
        {
            Feed(slice, play);
            // A coloured log's lines were held back: whose message each chat span is, and its channel,
            // are marked as the pane marks them before drawing (ChatColorizer.ApplyAll) - over the
            // whole Session at once, so a wrapped message's earlier rows see the channel its later
            // rows show. A plain log's went straight to the recorder.
            if (format is not null && play.Recorder is { } recorder)
            {
                var carry = default(ChatColorizer.Carry);
                ChatColorizer.ApplyAll(play.Held, persona, ref carry);
                foreach (var line in play.Held)
                    recorder.Append(line);
            }
        }
        finally
        {
            // On every path, failed ones included, the writer is closed: by the recorder once it is
            // stopped, or here when the game was never entered and no recorder was made.
            if (play.Recorder is { } recorder)
            {
                recorder.Stop(Local(play.EndMs));
                await recorder.Completion.ConfigureAwait(false);
            }
            else
                await writer.DisposeAsync().ConfigureAwait(false);
        }
        if (play.Recorder is null)
            throw new InvalidOperationException("The Session's recording never enters the game.");
        if (play.Recorder.Failure is { } failure)
            throw new IOException(failure);
    }

    /// <summary>The lines the Session viewer starts from: the lines the parser made between the entry
    /// and the exit, in order, as a pane is fed them - each chat span marked with its channel and
    /// whose message it is as the pane marks them, so a pane draws them in the player's chat colours.
    /// Each line still has the kind the parser gave it, which the viewer's filters judge
    /// (<see cref="LogFilter"/>). <see cref="TerminalBuffer.ScreenOf"/> makes them the text log's
    /// lines of screen. Synchronous; off the UI thread.</summary>
    /// <param name="persona">The persona whose own messages are marked (<see cref="ChatColorizer.IsSelf"/>).</param>
    /// <exception cref="InvalidOperationException">The recording never enters the game.</exception>
    public static List<StyledLine> Lines(SessionSlice slice, string? persona)
    {
        var play = new Play(holds: true, null);
        Feed(slice, play);
        if (!play.Began)
            throw new InvalidOperationException("The Session's recording never enters the game.");
        var carry = default(ChatColorizer.Carry);
        ChatColorizer.ApplyAll(play.Held, persona, ref carry);
        return play.Held;
    }

    private static void Feed(SessionSlice slice, Play play)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (slice.IsEmpty)
            throw new ArgumentException("The Session has no recording.", nameof(slice));
        var run = Muckrep.MaskLogin(slice.Run);
        using var session = MudSession.ForRecording();
        var inSlice = false;
        var now = 0L;
        session.GameModeEntered += () =>
        {
            if (inSlice && !play.Began)
                play.Begin(now);
        };
        session.GameModeExited += () =>
        {
            if (play.Writing)
                play.End(now);
        };
        session.LineReady += line =>
        {
            if (play.Writing)
                play.Take(line);
        };
        for (var i = 0; i <= slice.Last && !play.Ended; i++)
        {
            inSlice = i >= slice.First;
            var record = run[i];
            now = record.TimestampMs;
            // Already in the game where the slice starts: the entry was stamped into the slice before
            // (a Session with no recorded end is cut at the next one's start, and a record is stamped
            // when it is read, before the entry it holds is seen). The log starts here instead.
            if (i == slice.First && session.InGameMode && !play.Began)
                play.Begin(now);
            switch (record.Direction)
            {
                case WireDirection.Tx:
                    session.NoteRecordedSend(record.Payload, record.TimestampMs);
                    break;
                case WireDirection.Rx:
                    session.NoteRecordedTime(record.TimestampMs);
                    session.Feed(record.Payload);
                    if (!session.InGameMode)
                        session.EmitPartial();
                    break;
            }
        }
        // The recording stopped with the persona still in the game.
        if (play.Writing)
            play.End(run[slice.Last].TimestampMs);
    }

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>The persona's time in the game, as the feed meets it.</summary>
    /// <param name="holds">Keep the lines (<see cref="Held"/>) rather than streaming them to the
    /// recorder: an ANSI log and the viewer's lines are marked over the whole Session once it is
    /// read.</param>
    /// <param name="recorder">Makes the recorder at the entry, given its time; null keeps the lines
    /// only.</param>
    private sealed class Play(bool holds, Func<long, SessionRecorder>? recorder)
    {
        public SessionRecorder? Recorder { get; private set; }

        /// <summary>Every line from the entry to the exit, in order, when it holds them.</summary>
        public List<StyledLine> Held { get; } = [];

        public long EndMs { get; private set; }

        public bool Began { get; private set; }

        public bool Ended { get; private set; }

        public bool Writing => Began && !Ended;

        public void Begin(long ms)
        {
            EndMs = ms;   // until the end is met: a feed that fails still closes on a real time
            Began = true;
            Recorder = recorder?.Invoke(ms);
        }

        public void Take(StyledLine line)
        {
            if (holds)
                Held.Add(line);
            else
                Recorder!.Append(line);
        }

        public void End(long ms)
        {
            EndMs = ms;
            Ended = true;
        }
    }
}
