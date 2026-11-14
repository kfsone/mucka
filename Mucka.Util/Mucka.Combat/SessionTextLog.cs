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
/// face as ANSI SGR (<see cref="SgrText"/>) - written by the live transcript's own
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
/// <para>Synchronous parsing, then the recorder's own write; off the UI thread.</para>
/// </summary>
public static class SessionTextLog
{
    /// <summary>Writes the log to <paramref name="writer"/>, which it closes, whatever happens.</summary>
    /// <param name="colours">For an ANSI log: the chat colours and faces to draw with, the persona
    /// whose own messages they mark, and the colour depth. Null writes plain text.</param>
    /// <exception cref="IOException">The write failed.</exception>
    /// <exception cref="InvalidOperationException">The recording never enters the game.</exception>
    public static async Task WriteAsync(SessionSlice slice, string host, TextWriter writer,
        LogColours? colours = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Func<StyledLine, string>? format = colours is { } c ? line => SgrText.Format(line, c.Palette, c.Colours) : null;
        var play = new Play(writer, host, format);
        try
        {
            ArgumentNullException.ThrowIfNull(slice);
            if (slice.IsEmpty)
                throw new ArgumentException("The Session has no recording.", nameof(slice));
            Feed(Muckrep.MaskLogin(slice.Run), slice, play);
            // An ANSI log's lines were held back: whose message each chat span is, and its channel,
            // are marked as the pane marks them before drawing (ChatColorizer.ApplyAll) - over the
            // whole Session at once, so a wrapped message's earlier rows see the channel its later
            // rows show. A plain log's went straight to the recorder.
            if (colours is not null && play.Recorder is { } recorder)
            {
                var carry = default(ChatColorizer.Carry);
                ChatColorizer.ApplyAll(play.Held, colours.Persona, ref carry);
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

    private static void Feed(List<WireRecord> run, SessionSlice slice, Play play)
    {
        using var session = MudSession.ForRecording();
        var inSlice = false;
        var now = 0L;
        session.GameModeEntered += () =>
        {
            if (inSlice && play.Recorder is null)
                play.Begin(now);
        };
        session.GameModeExited += () =>
        {
            if (play.Writing)
                play.End(now);
        };
        session.LineReady += line =>
        {
            if (!play.Writing)
                return;
            if (play.HoldsLines)
                play.Held.Add(line);
            else
                play.Recorder!.Append(line);
        };
        for (var i = 0; i <= slice.Last && !play.Ended; i++)
        {
            inSlice = i >= slice.First;
            var record = run[i];
            now = record.TimestampMs;
            // Already in the game where the slice starts: the entry was stamped into the slice before
            // (a Session with no recorded end is cut at the next one's start, and a record is stamped
            // when it is read, before the entry it holds is seen). The log starts here instead.
            if (i == slice.First && session.InGameMode && play.Recorder is null)
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
    private sealed class Play(TextWriter writer, string host, Func<StyledLine, string>? format)
    {
        public SessionRecorder? Recorder { get; private set; }

        /// <summary>An ANSI log holds its lines back until the whole Session has been read.</summary>
        public bool HoldsLines => format is not null;

        /// <summary>Every line from the entry to the exit, in order, while <see cref="HoldsLines"/>.</summary>
        public List<StyledLine> Held { get; } = [];

        public long EndMs { get; private set; }

        public bool Ended { get; private set; }

        public bool Writing => Recorder is not null && !Ended;

        public void Begin(long ms)
        {
            EndMs = ms;   // until the end is met: a feed that fails still closes on a real time
            Recorder = new SessionRecorder(writer, host, Local(ms), format);
        }

        public void End(long ms)
        {
            EndMs = ms;
            Ended = true;
        }
    }
}
