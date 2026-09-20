using System.Text;
using System.Text.Json;
using MudSharp.Models;
using MudSharp.Session;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Tests for <see cref="SessionRecorder"/> - the plain-text session transcript
/// (docs/session-rec-design.md).
///
/// <para>The transcript records the SCREEN, so every assertion here is about the file agreeing with
/// what the pane would show: one line per committed line, the prompt written once no matter how many
/// times it was replaced, and the echo on the prompt's own row.</para>
/// </summary>
public sealed class SessionRecorderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mucka-rec-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static StyledLine Complete(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: false);

    private static StyledLine Partial(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: true);

    private static StyledLine Blank() => new(Array.Empty<StyledSpan>(), isPartial: false);

    private string NewPath() => Path.Combine(_dir, "rec.txt");

    /// <summary>Runs the body against a recorder, closes the file, and returns the body lines -
    /// everything between the opening line and the closing one.</summary>
    private async Task<string[]> RecordAsync(Action<SessionRecorder> body)
    {
        var path = NewPath();
        var recorder = new SessionRecorder(path, "mud2.co.uk", new DateTime(2026, 9, 14, 20, 30, 0));
        body(recorder);
        recorder.Stop();
        await recorder.Completion;

        var all = File.ReadAllLines(path);
        Assert.StartsWith("Recording by Mucka from mud2.co.uk starting on 2026-09-14 20:30:00.", all[0]);
        Assert.StartsWith("Recording by Mucka from mud2.co.uk finished on ", all[^1]);
        // Drop the header and its blank spacer, and the footer and its blank spacer.
        return all[2..^2];
    }

    // -- The file itself ------------------------------------------------------

    [Fact]
    public async Task The_file_and_its_directory_are_created_when_recording_is_armed()
    {
        // Arming is the one moment a human is watching the feature, which is why the constructor
        // opens the file rather than leaving it to the first line.
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");
        var path = Path.Combine(nested, "rec.txt");
        Assert.False(Directory.Exists(nested));

        var recorder = new SessionRecorder(path, "mud2.co.uk", DateTime.Now);
        Assert.True(File.Exists(path));
        Assert.Equal(path, recorder.Location);
        recorder.Stop();
        await recorder.Completion;
    }

    [Fact]
    public void The_file_name_carries_the_host_and_the_stamp_and_sanitises_the_host()
    {
        var at = new DateTime(2026, 9, 14, 20, 30, 15);
        Assert.Equal("session-rec.mud2.co.uk.20260914-203015.txt",
            SessionRecorder.BuildFileName("mud2.co.uk", at));
        // A host that would be an illegal file name still has to produce one.
        Assert.Equal("session-rec.a_b.20260914-203015.txt",
            SessionRecorder.BuildFileName("a/b", at));
        Assert.Equal("session-rec.unknown.20260914-203015.txt",
            SessionRecorder.BuildFileName("  ", at));
    }

    // -- What one line is -----------------------------------------------------

    [Fact]
    public async Task A_prompt_replaced_many_times_is_written_once()
    {
        // THE test. A prompt is a partial line the parser replaces until its echo completes it. A
        // recorder that wrote every StyledLine would put a copy of the prompt in the file for each
        // replacement; delegating to TerminalBuffer means the file gets exactly the one line the
        // screen ends up showing.
        var body = await RecordAsync(rec =>
        {
            rec.Append(Partial("*"));
            rec.Append(Partial("*"));
            rec.Append(Partial("*"));
            rec.Append(Complete("look"));
        });

        Assert.Equal(["*look"], body);
    }

    [Fact]
    public async Task The_echo_lands_on_the_prompt_row_and_the_reply_follows_it()
    {
        var body = await RecordAsync(rec =>
        {
            rec.Append(Partial("*"));
            rec.Append(Complete("wave"));
            rec.Append(Complete("OK, Ollie the warlock waves."));
            rec.Append(Partial("*"));
            rec.Append(Blank());
        });

        // The trailing blank completes the second prompt without merging anything into it.
        Assert.Equal(["*wave", "OK, Ollie the warlock waves.", "*"], body);
    }

    [Fact]
    public async Task A_live_prompt_is_kept_when_recording_stops_at_one()
    {
        // Stopping while sitting at a prompt: the prompt was on screen, so it is in the file.
        var body = await RecordAsync(rec =>
        {
            rec.Append(Complete("You are in a dark room."));
            rec.Append(Partial("*"));
        });

        Assert.Equal(["You are in a dark room.", "*"], body);
    }

    [Fact]
    public async Task A_form_feed_clears_the_screen_and_removes_nothing_from_the_file()
    {
        // The transcript is a record of what was shown. A clear-screen is not an unshowing, so the
        // lines before it stay and the file simply carries on.
        var body = await RecordAsync(rec =>
        {
            rec.Append(Complete("before the clear"));
            rec.Append(Complete("\f"));
            rec.Append(Complete("after the clear"));
        });

        Assert.Equal(["before the clear", "after the clear"], body);
    }

    [Fact]
    public async Task An_injected_annotation_sits_above_the_prompt_and_the_prompt_survives_it()
    {
        // The $f<n> F-key notes reach the pane through InjectAbovePartial, not the line stream.
        var body = await RecordAsync(rec =>
        {
            rec.Append(Partial("*"));
            rec.Inject(Complete("// s;s;s"));
            rec.Append(Complete("south"));
        });

        Assert.Equal(["// s;s;s", "*south"], body);
    }

    [Fact]
    public async Task Control_characters_are_stripped_and_tabs_expanded_as_the_pane_does_it()
    {
        var body = await RecordAsync(rec => rec.Append(Complete("a\tb\r")));

        // Tab to the 8-column stop; the CR of a CRLF ending has no glyph and is not written.
        Assert.Equal(["a       b"], body);
    }

    [Fact]
    public async Task Nothing_is_recorded_after_the_transcript_is_stopped()
    {
        var path = NewPath();
        var recorder = new SessionRecorder(path, "mud2.co.uk", DateTime.Now);
        recorder.Append(Complete("kept"));
        recorder.Stop();
        recorder.Append(Complete("dropped"));
        await recorder.Completion;

        var text = File.ReadAllText(path);
        Assert.Contains("kept", text);
        Assert.DoesNotContain("dropped", text);
    }

    [Fact]
    public async Task A_line_arriving_while_Stop_runs_never_lands_after_the_footer()
    {
        // The TCP read loop mid-Append while the UI thread stops the recording - the arrangement the
        // single-threaded test above cannot reach. Whatever the race decides, the footer must be the
        // last line: a line after it would be one the transcript claims arrived after it ended.
        //
        // This is a smoke test, not a proof. The window is a few instructions wide and hammering it
        // will not reliably open it; what actually closes it is Stop() queueing the footer under the
        // same lock the committing path holds. Kept because it does catch gross breakage - a line
        // written twice, a second footer, a torn file - under real contention.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var path = Path.Combine(_dir, $"race{attempt}.txt");
            var recorder = new SessionRecorder(path, "mud2.co.uk", DateTime.Now);
            using var gate = new ManualResetEventSlim(false);

            var appender = Task.Run(() =>
            {
                gate.Wait();
                for (var i = 0; i < 200; i++) recorder.Append(Complete($"line{i}"));
            });
            var stopper = Task.Run(() => { gate.Wait(); recorder.Stop(); });

            gate.Set();
            await Task.WhenAll(appender, stopper);
            await recorder.Completion;

            var all = File.ReadAllLines(path);
            Assert.StartsWith("Recording by Mucka from mud2.co.uk finished on ", all[^1]);
            Assert.DoesNotContain(all[..^1], l => l.StartsWith("Recording by Mucka from mud2.co.uk finished"));
        }
    }

    /// <summary>A writer that accepts the header and then fails, standing in for a disk that fills
    /// mid-session or a network path that goes away.</summary>
    private sealed class FailsAfterHeader : TextWriter
    {
        public bool Disposed { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) { }
        public override void WriteLine() { }
        public override Task WriteLineAsync(string? value)
            => throw new IOException("There is not enough space on the disk.");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task A_write_failure_reports_itself_and_ends_the_recording()
    {
        // Without this the file stops growing, the chip stays lit, and the operator goes on believing
        // he is recording while every later line piles up in a queue nothing is reading. Completion
        // must also still COMPLETE: teardown awaits it, and a faulted task there would take the store
        // and the wire log's close down with it.
        string? reported = null;
        var writer = new FailsAfterHeader();
        var recorder = new SessionRecorder(NewPath(), "mud2.co.uk", DateTime.Now,
            m => reported = m, writer);

        recorder.Append(Complete("this cannot be written"));
        await recorder.Completion;          // must complete, not fault

        Assert.Equal("There is not enough space on the disk.", reported);
        Assert.Equal("There is not enough space on the disk.", recorder.Failure);
        Assert.True(writer.Disposed);

        // The recording is over: further lines are refused rather than queued against no reader.
        recorder.Append(Complete("and neither can this"));
        await recorder.Completion;
    }

    // -- Against real traffic -------------------------------------------------


    /// <summary>Undoes the three escapes a <c>.c1</c> payload carries: a backslash, and the CR and
    /// LF that would otherwise split a record. Latin-1, so a char IS its byte.</summary>
    private static byte[] CaptureBytes(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length)
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            else
                bytes.Add((byte)payload[i]);
        }
        return bytes.ToArray();
    }

    private static readonly string CaptureFile =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.c1");

    [Fact]
    public async Task A_real_capture_replays_into_a_transcript_of_the_screen()
    {
        // The six wyvern frames through the production parser and into the recorder: the line model
        // is measured against real MUD2 traffic, prompts and all, rather than against hand-built
        // StyledLines that agree with it by construction.
        var path = NewPath();
        var recorder = new SessionRecorder(path, "mud2.co.uk", DateTime.Now);
        var streamed = new List<StyledLine>();

        using (var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        }))
        {
            session.LineReady += line => { streamed.Add(line); recorder.Append(line); };
            // One record per line: ts_ms, direction, then the server's own bytes - see CaptureBytes.
            foreach (var rawLine in File.ReadLines(CaptureFile, Encoding.Latin1))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                var parts = rawLine.Split(' ', 3);
                if (parts.Length < 3 || parts[1] != "rx") continue;
                session.Feed(CaptureBytes(parts[2]));
            }
        }

        recorder.Stop();
        await recorder.Completion;
        var body = File.ReadAllLines(path)[2..^2];

        // The fight's own lines are all present, in order.
        Assert.Contains(body, l => l.Contains("The wyvern is staring at you ferociously."));
        Assert.Contains(body, l => l.Contains("The wyvern stings you with its venomous tail."));
        Assert.Contains(body, l => l.Contains("drops dead, poisoned"));
        Assert.True(Array.FindIndex(body, l => l.Contains("staring at you ferociously"))
                    < Array.FindIndex(body, l => l.Contains("drops dead, poisoned")));

        // Each frame ends at a prompt that is still live when the next frame opens, so the prompt
        // merges into the head of the next frame's first line - exactly what the pane shows, and the
        // reason this test reads the lines with Contains rather than by equality.
        Assert.Contains("*The wyvern stings you with its venomous tail.", body);

        // The capture really does deliver prompts as repeated partials, so the count below is
        // measuring something: the stream carries far more StyledLines than the file has lines.
        Assert.True(streamed.Count(l => l.IsPartial) > 0);
        Assert.True(streamed.Count > body.Length);

        // Exactly one bare prompt in the whole file, and it is the last line: the live prompt the
        // capture ends on, which Stop writes because it was on screen. Every other prompt was merged
        // into the line that completed it. None was written twice.
        Assert.Equal(1, body.Count(l => l == "*"));
        Assert.Equal("*", body[^1]);
    }
}
