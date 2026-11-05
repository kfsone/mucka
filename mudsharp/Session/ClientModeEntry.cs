namespace MudSharp.Session;

/// <summary>
/// What the client sends once, at the Option menu, to put the shell in the mode the rest of the
/// session assumes.
/// </summary>
public static class ClientModeEntry
{
    /// <summary>
    /// <c>ESC-[ ESC^F ESC-N /F+ ESC-]</c>: a command interrupt (input the server runs without
    /// echoing it) that enters best-client mode with C1 codes, selects normal mode, and turns local
    /// formatting on, which stops the server wrapping the text it sends. The terminal width is never
    /// sent: the pane wraps the text itself, at whatever width it has.
    ///
    /// <para>mud2_FE4.txt names the mode ESC-N selects: "normal mode (user defines wrap etc.)". The
    /// client has always sent it with the width, and whether <c>/F+</c> works without it has not been
    /// tried.</para>
    /// </summary>
    public static ReadOnlySpan<byte> Bytes =>
    [
        0x1B, 0x2D, 0x5B,                  // ESC-[
        0x1B, 0x06,                        // ESC^F
        0x1B, 0x2D, 0x4E,                  // ESC-N
        (byte)'/', (byte)'F', (byte)'+',
        0x1B, 0x2D, 0x5D,                  // ESC-]
    ];
}
