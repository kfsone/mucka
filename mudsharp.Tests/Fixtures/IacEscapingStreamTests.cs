using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

public class IacEscapingStreamTests
{
    private static byte[] ReadAll(byte[] source, int readSize)
    {
        using var stream = new IacEscapingStream(new MemoryStream(source));
        var result = new List<byte>();
        var buf = new byte[readSize];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            result.AddRange(buf.Take(n));
        return result.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4096)]
    public void EveryFf_ArrivesDoubled_WhateverTheReadSize(int readSize)
    {
        var source = new byte[] { 0x9B, 0xFF, 0xFA, 0xFF, 0xFF, (byte)'x', 0xFF };
        var expected = new byte[] { 0x9B, 0xFF, 0xFF, 0xFA, 0xFF, 0xFF, 0xFF, 0xFF, (byte)'x', 0xFF, 0xFF };
        Assert.Equal(expected, ReadAll(source, readSize));
    }

    [Fact]
    public async Task ReadAsync_DoublesAndReportsEndOfStream()
    {
        using var stream = new IacEscapingStream(new MemoryStream(new byte[] { 0xFF, (byte)'a' }));
        var buf = new byte[8];
        var n = await stream.ReadAsync(buf);
        Assert.Equal(new byte[] { 0xFF, 0xFF, (byte)'a' }, buf.Take(n).ToArray());
        Assert.Equal(0, await stream.ReadAsync(buf));
    }

    [Fact]
    public void Writes_PassThroughUndoubled()
    {
        var sink = new MemoryStream();
        using var stream = new IacEscapingStream(sink);
        stream.Write(new byte[] { 0xFF, (byte)'a' }, 0, 2);
        Assert.Equal(new byte[] { 0xFF, (byte)'a' }, sink.ToArray());
    }

    [Fact]
    public void SshOptionPromptWithWidthConfirmation_IsRecognisedAfterDoubling()
    {
        // The server's reply to the client-mode entry, as an SSH connection delivered it: the C1 code
        // carrying the licence block ends in a lone 0xFF, and the line after it is the width
        // confirmation followed by the shell's Option prompt.
        var reply = new List<byte>();
        reply.AddRange("\x1B[1;37;40m"u8.ToArray());
        reply.AddRange(new byte[] { 0x9B, 0xFF, 0xFA, 0xFF });
        reply.AddRange("57009120\r\n1\r\n1\r\nz00012305\r\n1\r\n"u8.ToArray());
        reply.Add(0xFF);
        reply.AddRange("[New terminal width is 90]\r\nOption (H for help): "u8.ToArray());

        var h = new ParserHarness();
        h.Feed(ReadAll(reply.ToArray(), 4096));
        h.Parser.ShowPrompt();

        Assert.Equal(new[] { 90 }, h.ConfirmedWidths);
        Assert.Contains(h.Lines, l => l.PlainText.Contains("Option (H for help):"));
    }
}
