namespace MudSharp.Protocol;

/// <summary>
/// Reads the server's bytes as a telnet connection delivers them: every 0xFF doubled. The parser
/// takes a lone 0xFF as the start of a telnet command, but the server's C1 codes end in 0xFF, and
/// only telnetd's escaping makes those arrive as FF FF. Over SSH nothing doubles them, so a C1 code
/// such as the one carrying the Option prompt's width confirmation reaches the parser as
/// "9B FF FA FF ..." instead of "9B FF FF FA FF FF ...". Writes pass through untouched.
/// </summary>
public sealed class IacEscapingStream : Stream
{
    private readonly Stream _inner;
    // The second FF of a pair whose first half filled the caller's buffer.
    private bool _pendingFf;
    // One reader at a time, so one buffer is reused for every read.
    private byte[] _scratch = new byte[1];

    public IacEscapingStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_pendingFf)
        {
            _pendingFf = false;
            buffer.Span[0] = 0xFF;
            return 1;
        }

        // Half the room, so a read of nothing but FF still fits once doubled.
        var raw = Scratch(buffer.Length);
        var n = await _inner.ReadAsync(raw.AsMemory(0, Math.Max(1, buffer.Length / 2)), cancellationToken).ConfigureAwait(false);
        return Expand(raw.AsSpan(0, n), buffer.Span);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (_pendingFf)
        {
            _pendingFf = false;
            buffer[0] = 0xFF;
            return 1;
        }

        var raw = Scratch(buffer.Length);
        var n = _inner.Read(raw, 0, Math.Max(1, buffer.Length / 2));
        return Expand(raw.AsSpan(0, n), buffer);
    }

    private byte[] Scratch(int roomLength)
    {
        var size = Math.Max(1, roomLength / 2);
        if (_scratch.Length < size) _scratch = new byte[size];
        return _scratch;
    }

    private int Expand(ReadOnlySpan<byte> raw, Span<byte> dest)
    {
        var o = 0;
        foreach (var b in raw)
        {
            dest[o++] = b;
            if (b != 0xFF) continue;
            if (o < dest.Length) dest[o++] = 0xFF;
            else _pendingFf = true;
        }
        return o;
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
