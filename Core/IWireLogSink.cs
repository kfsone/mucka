namespace Mucka.Core;

/// <summary>
/// Which side of the socket a wire record came from, or that it is not wire traffic at all.
/// The numeric values are STORED (they are packed into the batch framing - see
/// <see cref="WireLogFraming"/>) so they may never be renumbered.
/// </summary>
public enum WireDirection : byte
{
    /// <summary>Bytes the server sent us, exactly as they came off the socket.</summary>
    Rx = 0,
    /// <summary>Bytes we sent the server, exactly as they went onto the socket.</summary>
    Tx = 1,
    /// <summary>A client-side note, not wire traffic. UTF-8 text - see <see cref="WireRecord"/>.</summary>
    Annotation = 2,
}

/// <summary>
/// One record in the wire log: when, which direction, and the bytes.
///
/// <para><b>The payload is bytes, not a string, and that is the whole point.</b> MUD2's C1 codes are
/// high bytes; the moment a record becomes a .NET string it has picked an encoding, and the moment it
/// becomes JSON each of those high bytes costs six characters (<c>0xFF</c>). Measured over 40 existing
/// session recordings: 6.1 MB of actual payload became 17.2 MB of .jsonl, a 2.8x tax paid entirely on
/// escaping. Sinks that need text decode at the edge; storage never does.</para>
///
/// <para>The encoding contract, for a sink that must produce text: <see cref="WireDirection.Rx"/> and
/// <see cref="WireDirection.Tx"/> payloads are raw wire bytes and decode with Latin-1 (the identity map
/// for a byte-oriented 1980 protocol, and the one the parser itself uses).
/// <see cref="WireDirection.Annotation"/> payloads are UTF-8 of a client-side string - UTF-8 rather
/// than Latin-1 so an annotation is lossless for any .NET string, not just the ones that happen to
/// fit in a byte.</para>
/// </summary>
/// <param name="TimestampMs">Unix milliseconds, taken as the record was handed to the capture.</param>
public readonly record struct WireRecord(long TimestampMs, WireDirection Direction, byte[] Payload);

/// <summary>
/// A destination for the wire log. Exists so the backend can be swapped without touching the capture
/// path: <see cref="SqliteWireLogSink"/> is the compressed-blob store this was built for, and
/// <see cref="JsonlWireLogSink"/> is the pre-existing one-line-per-record text file, which several
/// tests and the whole offline analysis corpus still read.
///
/// <para><b>Threading.</b> <see cref="Record"/> is called from the connection's read loop AND its write
/// loop - two different threads, concurrently, neither of them the UI thread but both of them on the
/// path that delivers game text. Implementations must be thread-safe and must not do I/O or compression
/// on the caller's thread; buffer and hand off.</para>
/// </summary>
public interface IWireLogSink : IDisposable
{
    /// <summary>Where this sink is writing - a file path, a database path. Shown to the player.</summary>
    string Location { get; }

    /// <summary>Appends one record. Must be cheap and thread-safe; see the threading note above.</summary>
    void Record(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Pushes everything buffered as far towards durable as this sink can, without closing it.
    /// Called on disconnect and on stop. Best-effort: a sink must never throw out of this.
    /// </summary>
    void Flush();
}
