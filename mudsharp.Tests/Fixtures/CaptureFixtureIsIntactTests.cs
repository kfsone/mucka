using System.Security.Cryptography;
using System.Text;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The committed capture is EVIDENCE, and nothing else in the suite would notice it changing.
///
/// <para>Every test that reads <c>wyvern-poison-death.c1</c> asserts a parsed outcome - that the
/// fight opens and closes, that a line is <c>FightEnd</c>, that a sting produces no event. A byte
/// could move inside a frame and all of them would stay green, because none of them looks at bytes.
/// This one does: it pins the file's digest, its record count and its exact payload lengths, so a
/// rewrite by an editor, a line-ending conversion or a well-meaning reformat fails loudly and names
/// itself.</para>
///
/// <para>Updating these numbers is allowed only when the capture is deliberately re-cut from the
/// source recording. It is not a number to make green again.</para>
/// </summary>
public class CaptureFixtureIsIntactTests
{
    private static readonly string CaptureFile =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.c1");

    /// <summary>SHA-256 of the file as committed, lower-case hex.</summary>
    private const string ExpectedDigest =
        "e042e9b69e84e775722caaa3fe974b041d4c8ad31aa64032d76286fa0895f877";

    private const int ExpectedRecords = 6;
    private const int ExpectedPayloadBytes = 763;

    /// <summary>Each record's payload length, in order. A byte moving BETWEEN two frames would keep
    /// the total and the digest of a naive concatenation identical; these do not.</summary>
    private static readonly int[] ExpectedLengths = [63, 74, 199, 144, 154, 129];

    [Fact]
    public void TheCaptureIsByteForByteWhatWasCommitted()
    {
        Assert.True(File.Exists(CaptureFile), "capture missing: " + CaptureFile);
        var bytes = File.ReadAllBytes(CaptureFile);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(ExpectedDigest, digest);
    }

    [Fact]
    public void TheCaptureStillHoldsSixFramesOfTheExpectedSizes()
    {
        var records = ReadCapture(CaptureFile);

        Assert.Equal(ExpectedRecords, records.Count);
        Assert.Equal(ExpectedLengths, records.Select(r => r.Payload.Length).ToArray());
        Assert.Equal(ExpectedPayloadBytes, records.Sum(r => r.Payload.Length));
        Assert.All(records, r => Assert.Equal("rx", r.Direction));
    }

    [Fact]
    public void TheFramesStillCarryTheC1CodesThatMakeThemWorthKeeping()
    {
        // The whole reason this capture is bytes and not a transcript: a third of it is above 0x7F.
        // A conversion that "cleaned up" the file would strip exactly these and leave the prose.
        var payload = ReadCapture(CaptureFile).SelectMany(r => r.Payload).ToArray();

        Assert.Equal(220, payload.Count(b => b >= 0x80));
        Assert.Contains((byte)0x00, payload);   // the CR-NUL-CRLF line framing
    }

    private readonly record struct CaptureRecord(long TimestampMs, string Direction, byte[] Payload);

    private static List<CaptureRecord> ReadCapture(string path)
    {
        var records = new List<CaptureRecord>();
        foreach (var line in File.ReadAllLines(path, Encoding.Latin1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(' ', 3);
            records.Add(new CaptureRecord(long.Parse(parts[0]), parts[1], CaptureBytes(parts[2])));
        }
        return records;
    }

    /// <summary>The decode, reimplemented here rather than shared: this test exists to catch the
    /// others being wrong, and a decoder it borrowed from them could not.</summary>
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

    // -- The decode itself, which nothing else exercises directly ----------------

    [Theory]
    [InlineData("abc", new byte[] { 0x61, 0x62, 0x63 })]
    [InlineData(@"a\rb", new byte[] { 0x61, 0x0D, 0x62 })]
    [InlineData(@"a\nb", new byte[] { 0x61, 0x0A, 0x62 })]
    [InlineData(@"a\\b", new byte[] { 0x61, 0x5C, 0x62 })]
    // The case that proves the decode is prefix-free: an escaped backslash followed by a literal
    // 'r' must NOT be read as an escaped CR.
    [InlineData(@"a\\rb", new byte[] { 0x61, 0x5C, 0x72, 0x62 })]
    [InlineData("", new byte[0])]
    public void TheDecodeIsUnambiguous(string encoded, byte[] expected)
        => Assert.Equal(expected, CaptureBytes(encoded));

    [Fact]
    public void AHighByteSurvivesTheDecodeUntouched()
    {
        // Latin-1: the char IS the byte. This is what lets the C1 codes sit in the file raw.
        var encoded = new string([(char)0xA3, (char)0x9B, (char)0xFF]);
        Assert.Equal(new byte[] { 0xA3, 0x9B, 0xFF }, CaptureBytes(encoded));
    }
}
