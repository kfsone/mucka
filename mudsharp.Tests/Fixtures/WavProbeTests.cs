using Mucka.Core;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The WAV timing probe the combat metronome's bracket depends on.
///
/// <para>Worth pinning because the dependency is invisible from the audio code: if
/// <see cref="WavProbe.Parse"/> returns null, the metronome silently falls back to scheduling by the
/// bracket offset alone, which is the doubled-hit overlap two shipped versions already had. A parser
/// that fails quietly is the worst shape for this, so these tests cover the refusals as carefully as the
/// successes.</para>
/// </summary>
public class WavProbeTests
{
    /// <summary>Builds a PCM WAV in memory. <paramref name="frames"/> is a per-frame amplitude in
    /// 0..1, so a test can state an envelope directly.</summary>
    private static byte[] Wav(
        IReadOnlyList<double> frames, int sampleRate = 48000, int bits = 16, int channels = 2,
        short formatTag = 1, string dataId = "data", bool truncate = false, bool extraChunk = false)
    {
        var bytesPerSample = bits / 8;
        var blockAlign = channels * bytesPerSample;
        var data = new byte[frames.Count * blockAlign];
        for (var i = 0; i < frames.Count; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var off = (i * blockAlign) + (c * bytesPerSample);
                if (bits == 16)
                {
                    var v = (short)Math.Round(Math.Clamp(frames[i], -1, 1) * 32767);
                    data[off] = (byte)(v & 0xFF);
                    data[off + 1] = (byte)((v >> 8) & 0xFF);
                }
                else
                {
                    // 8-bit WAV is UNSIGNED, silence at 128 - the trap this probe has to get right.
                    data[off] = (byte)Math.Clamp(128 + (int)Math.Round(frames[i] * 127), 0, 255);
                }
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF".ToCharArray());
        w.Write(0);                                  // patched below
        w.Write("WAVE".ToCharArray());

        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write(formatTag);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);            // byte rate
        w.Write((short)blockAlign);
        w.Write((short)bits);

        if (extraChunk)
        {
            // A LIST chunk between fmt and data, which real exports carry and a fixed-offset reader
            // would trip over.
            w.Write("LIST".ToCharArray());
            w.Write(4);
            w.Write("INFO".ToCharArray());
        }

        w.Write(dataId.ToCharArray());
        w.Write(truncate ? data.Length + 500 : data.Length);
        w.Write(data);
        w.Flush();

        var bytes = ms.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>A run of silence, then a short loud body, then a long quiet tail - the shape of the real
    /// Perc_Stick assets after padding, and the shape that broke two versions of the scheduler.</summary>
    private static IReadOnlyList<double> ClickEnvelope(
        int silenceFrames, int bodyFrames, int tailFrames, double tailLevel = 0.02)
    {
        var f = new List<double>(silenceFrames + bodyFrames + tailFrames);
        f.AddRange(Enumerable.Repeat(0.0, silenceFrames));
        f.AddRange(Enumerable.Repeat(1.0, bodyFrames));
        f.AddRange(Enumerable.Repeat(tailLevel, tailFrames));   // well under -20 dB of peak
        return f;
    }

    [Fact]
    public void FindsTheAudibleSpanAndIgnoresTheInaudibleTail()
    {
        // 48 kHz: 1 ms = 48 frames. 30 ms silence, 36 ms body, 134 ms tail - the real click's shape.
        var span = WavProbe.Parse(Wav(ClickEnvelope(30 * 48, 36 * 48, 134 * 48)));

        Assert.NotNull(span);
        Assert.Equal(200.0, span!.Value.TotalMs, 1);
        Assert.Equal(30.0, span.Value.AudibleStartMs, 1);
        Assert.Equal(66.0, span.Value.AudibleEndMs, 1);
        Assert.Equal(36.0, span.Value.AudibleBodyMs, 1);
    }

    [Fact]
    public void TheTailIsWhatMattered_SoAssertItIsExcluded()
    {
        // The bug this class exists to prevent: scheduling by TotalMs put the audible content a whole
        // tail-length earlier than intended. The span must not be the file.
        var span = WavProbe.Parse(Wav(ClickEnvelope(30 * 48, 36 * 48, 134 * 48)))!.Value;
        Assert.True(span.TotalMs - span.AudibleEndMs > 100,
            "the test envelope is supposed to have a long inaudible tail, or it is not testing anything");
    }

    [Fact]
    public void EightBitIsReadAsUnsigned()
    {
        // Silence at 128, not 0. Read as signed, a silent 8-bit lead-in looks like full scale and the
        // probe would report no leading silence for every 8-bit asset in the game.
        var span = WavProbe.Parse(Wav(ClickEnvelope(220, 220, 220), sampleRate: 22050, bits: 8));

        Assert.NotNull(span);
        // 220 frames at 22.05 kHz. The END is the LAST audible frame (index 439), not the first silent
        // one, so it is 19.9 ms rather than a round 20 - an off-by-one-frame the first version of this
        // test got wrong, in the test rather than the code.
        Assert.Equal(10.0, span!.Value.AudibleStartMs, 1);
        Assert.Equal(19.9, span.Value.AudibleEndMs, 1);
    }

    [Fact]
    public void WalksPastChunksBetweenFmtAndData()
    {
        var span = WavProbe.Parse(Wav(ClickEnvelope(48, 480, 48), extraChunk: true));
        Assert.NotNull(span);
        Assert.Equal(1.0, span!.Value.AudibleStartMs, 1);
    }

    /// <summary>
    /// Content below the relative floor does not extend the span. The peak itself defines the floor, so
    /// this needs a loud frame plus a quiet region rather than a uniformly quiet clip.
    ///
    /// <para>Deliberately does NOT test a level of exactly 0.1 (the floor). At 16-bit,
    /// <c>0.1 * 32767</c> rounds to 3277, which is a hair ABOVE a floor derived from a peak of
    /// 32767/32768 - so an exactly-at-floor test asserts a property of the builder's rounding rather
    /// than of the probe. The first version of this test did that and failed for that reason.</para>
    /// </summary>
    [Theory]
    [InlineData(0.09)]
    [InlineData(0.05)]
    [InlineData(0.0)]
    public void ContentBelowTheFloorIsNotAudible(double level)
    {
        var frames = new List<double> { 1.0 };
        frames.AddRange(Enumerable.Repeat(level, 480));
        var span = WavProbe.Parse(Wav(frames))!.Value;

        // Only the single loud frame counts, so the span collapses to ~0 ms.
        Assert.Equal(0.0, span.AudibleStartMs, 1);
        Assert.Equal(0.0, span.AudibleEndMs, 1);
    }

    // ── Refusals. Each of these silently degrades the metronome if it returns a wrong answer. ──

    [Fact]
    public void RefusesNonPcm()
    {
        // clio.1324.wav in this project is MP3 inside a WAV wrapper (format tag 0x55). Reading its
        // compressed payload as samples would produce a confident, meaningless span.
        Assert.Null(WavProbe.Parse(Wav(ClickEnvelope(48, 48, 48), formatTag: 0x0055)));
    }

    [Fact]
    public void RefusesATruncatedFile()
        => Assert.Null(WavProbe.Parse(Wav(ClickEnvelope(48, 48, 48), truncate: true)));

    [Fact]
    public void RefusesAFileWithNoDataChunk()
        => Assert.Null(WavProbe.Parse(Wav(ClickEnvelope(48, 48, 48), dataId: "junk")));

    [Fact]
    public void RefusesASilentClip()
        => Assert.Null(WavProbe.Parse(Wav(Enumerable.Repeat(0.0, 480).ToList())));

    [Fact]
    public void RefusesABitDepthItDoesNotHandle()
        => Assert.Null(WavProbe.Parse(Wav(ClickEnvelope(48, 48, 48), bits: 24)));

    [Theory]
    [InlineData("")]
    [InlineData("RIFF")]
    [InlineData("RIFFxxxxWAVE")]
    [InlineData("not a wav at all")]
    public void RefusesGarbageWithoutThrowing(string junk)
        => Assert.Null(WavProbe.Parse(System.Text.Encoding.ASCII.GetBytes(junk)));

    [Fact]
    public void ReadReturnsNullForAMissingFile()
        => Assert.Null(WavProbe.Read(Path.Combine(Path.GetTempPath(), "mucka-no-such-file-9d3f.wav")));

    // ── The arithmetic the metronome does with the result ──────────────────────────────────────

    /// <summary>
    /// The bracket the owner specified: 100 ms of silence between the END of the first sound and the
    /// START of the second, centred on the tick boundary. This reproduces CombatMetronome's two offset
    /// calculations against a real-shaped clip and checks the perceived result, which is the thing that
    /// was wrong in play twice.
    /// </summary>
    [Fact]
    public void ScheduledFromTheSpan_ThePerceivedGapIsExactlyTwoN_CentredOnTheBoundary()
    {
        const double n = 50.0;
        var pre = WavProbe.Parse(Wav(ClickEnvelope(30 * 48, 36 * 48, 134 * 48)))!.Value;
        var post = pre;   // the two click assets are the same shape

        // CombatMetronome.PreTickLeadMilliseconds / AfterTickOffsetMilliseconds
        var preLead = n + pre.AudibleEndMs;
        var afterOffset = Math.Max(1.0, n - post.AudibleStartMs);

        // Where each clip's audible content lands, relative to the boundary at 0.
        var preAudibleEnd = -preLead + pre.AudibleEndMs;
        var postAudibleStart = afterOffset + post.AudibleStartMs;

        Assert.Equal(-n, preAudibleEnd, 6);
        Assert.Equal(n, postAudibleStart, 6);
        Assert.Equal(2 * n, postAudibleStart - preAudibleEnd, 6);
        // The boundary is the midpoint of the silence, which is what "centred on the cycle" means.
        Assert.Equal(0.0, (preAudibleEnd + postAudibleStart) / 2, 6);
    }
}
