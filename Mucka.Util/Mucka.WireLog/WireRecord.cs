namespace Mucka.WireLog;

/// <summary>
/// Which side of the socket a wire record came from, or that it is not wire traffic at all.
/// The numeric values are STORED - they are <c>wire.direction</c> - so they may never be renumbered.
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
/// high bytes; the moment a record becomes a .NET string it has picked an encoding. Measured over 40
/// session recordings: 6.1 MB of actual payload became 17.2 MB of .jsonl, a 2.8x tax paid entirely on
/// escaping. Consumers that need text decode at the edge; storage never does.</para>
///
/// <para>The encoding contract, for a consumer that must produce text: <see cref="WireDirection.Rx"/>
/// and <see cref="WireDirection.Tx"/> payloads are raw wire bytes and decode with Latin-1 (the identity
/// map for a byte-oriented 1980 protocol, and the one the parser itself uses).
/// <see cref="WireDirection.Annotation"/> payloads are UTF-8 of a client-side string - UTF-8 rather
/// than Latin-1 so an annotation is lossless for any .NET string, not just the ones that happen to
/// fit in a byte.</para>
/// </summary>
/// <param name="TimestampMs">Unix milliseconds, taken as the record was handed to the writer.</param>
public readonly record struct WireRecord(long TimestampMs, WireDirection Direction, byte[] Payload);
