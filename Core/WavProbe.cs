namespace Mucka.Core;

/// <summary>Where a WAV asset's audible content sits inside it. All three figures are milliseconds from
/// the start of the file.</summary>
/// <param name="TotalMs">The whole clip.</param>
/// <param name="AudibleStartMs">First sample above the floor - i.e. the leading silence.</param>
/// <param name="AudibleEndMs">Last sample above the floor.</param>
public readonly record struct ClipSpan(double TotalMs, double AudibleStartMs, double AudibleEndMs)
{
    /// <summary>How long the audible part lasts.</summary>
    public double AudibleBodyMs => AudibleEndMs - AudibleStartMs;
}

/// <summary>
/// Reads timing out of a PCM WAV file: its total length, and the span within it that a listener can
/// actually hear.
///
/// <para><b>Why this exists as its own class.</b> The combat metronome brackets a tick boundary with two
/// clicks, and what a player perceives as the bracket is where the SOUND is, not where the FILE is. The
/// click assets run 199.6 ms while their audible content spans 30-66 ms, so anything scheduling by file
/// length is out by the ~134 ms of inaudible tail. Two shipped versions were wrong on exactly this - one
/// compensating by nothing and one by the total length - and both were audible in play. It is separate
/// from <c>SoundService</c> because that class is Windows-only and full of WinRT, whereas this is pure
/// byte arithmetic that the test project can link and exercise directly. The thing the metronome's
/// timing now depends on ought to be testable.</para>
///
/// <para><b>The audible floor is a perceptual judgement, named as one:</b> -20 dB relative to the
/// clip's OWN peak (<see cref="AudibleFloor"/>). Relative rather than absolute so it keeps its meaning
/// if an asset is re-levelled. Deliberately not the digital-silence floor used to detect padding
/// (<c>tools/combat/pad_click_samples.py</c> uses ~-54 dBFS for that): these clicks decay into a long
/// tail that is present in the data and inaudible in a room, and counting that tail as content is the
/// error above.</para>
///
/// <para>File I/O, so call it once and off the UI thread. Deterministic and correct on the first call,
/// unlike WinRT's <c>PlaybackSession.NaturalDuration</c>, which is null until an async media-open lands
/// and can only be reached by touching or constructing a player.</para>
/// </summary>
internal static class WavProbe
{
    /// <summary>Amplitude ratio to the clip's own peak below which a sample is treated as inaudible.
    /// 0.1 is -20 dB.</summary>
    internal const double AudibleFloor = 0.1;

    /// <summary>Reads <paramref name="path"/>, or null if it is missing or unparseable.</summary>
    public static ClipSpan? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WavProbe] {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses WAV bytes. Null for anything it cannot read honestly - a truncated file, a non-PCM codec,
    /// a bit depth it does not handle, or a clip that is silent throughout.
    ///
    /// <para>Walks the chunk list rather than assuming fmt-then-data at fixed offsets: a WAV may carry
    /// LIST/fact/cue chunks between them and exported assets routinely do. One asset in this project
    /// (<c>clio.1324.wav</c>) is MP3 inside a WAV wrapper, which this refuses rather than reading the
    /// compressed payload as samples.</para>
    /// </summary>
    public static ClipSpan? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12
            || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            return null;

        int channels = 0, bits = 0, sampleRate = 0, blockAlign = 0;
        var formatTag = 0;
        var pos = 12;

        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes.Slice(pos, 4));
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + 4, 4));
            var body = pos + 8;
            if (size < 0 || body + size > bytes.Length)
                return null;                                   // truncated

            if (id == "fmt " && size >= 16)
            {
                var f = bytes.Slice(body, 16);
                formatTag = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(f);
                channels = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(f[2..]);
                sampleRate = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
                blockAlign = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(f[12..]);
                bits = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(f[14..]);
            }
            else if (id == "data")
            {
                // 1 is WAVE_FORMAT_PCM. Anything else (0x55 is MP3-in-WAV) is not samples.
                if (formatTag != 1 || sampleRate <= 0 || blockAlign <= 0 || channels <= 0)
                    return null;
                if (bits is not (8 or 16))
                    return null;                               // refuse rather than guess at 24/32-bit
                if (blockAlign < channels * (bits / 8))
                    return null;
                return Measure(bytes.Slice(body, size), channels, bits, sampleRate, blockAlign);
            }

            pos = body + size + (size % 2);                    // chunks are word-aligned
        }
        return null;                                           // no data chunk
    }

    private static ClipSpan? Measure(
        ReadOnlySpan<byte> data, int channels, int bits, int sampleRate, int blockAlign)
    {
        var frames = data.Length / blockAlign;
        if (frames <= 0)
            return null;

        var peak = 0.0;
        for (var i = 0; i < frames; i++)
        {
            var v = Level(data, i, channels, bits, blockAlign);
            if (v > peak) peak = v;
        }
        if (peak <= 0)
            return null;                                       // silent throughout

        var floor = peak * AudibleFloor;
        int first = -1, last = -1;
        for (var i = 0; i < frames; i++)
        {
            if (Level(data, i, channels, bits, blockAlign) <= floor)
                continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0)
            return null;

        var msPerFrame = 1000.0 / sampleRate;
        return new ClipSpan(frames * msPerFrame, first * msPerFrame, last * msPerFrame);
    }

    /// <summary>Peak amplitude of one frame across all channels, 0..1.
    ///
    /// <para><b>8-bit WAV is UNSIGNED with silence at 128; 16-bit is signed with silence at 0.</b>
    /// Reading an 8-bit file as signed makes every one of them look like it starts at full scale - which
    /// would have this class report the very fault it is used to diagnose, on every 8-bit asset in the
    /// game. Most of this project's sounds are 8-bit.</para>
    ///
    /// <para>Takes the span as a parameter rather than closing over it: a <c>ReadOnlySpan</c> cannot be
    /// captured by a local function.</para></summary>
    private static double Level(
        ReadOnlySpan<byte> data, int frame, int channels, int bits, int blockAlign)
    {
        var off = frame * blockAlign;
        var worst = 0.0;
        for (var c = 0; c < channels; c++)
        {
            var v = bits == 16
                ? Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                    data.Slice(off + (c * 2), 2))) / 32768.0
                : Math.Abs(data[off + c] - 128) / 128.0;
            if (v > worst) worst = v;
        }
        return worst;
    }
}
