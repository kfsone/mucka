namespace MudSharp.Protocol;

/// <summary>
/// Handles IAC telnet option negotiation for the MUD2 protocol.
/// Derived from MudCPP, see also Clio telnet.l (lines 206–275).
/// </summary>
internal sealed class TelnetNegotiator
{
    private readonly Action<byte[]> _send;

    // One-shot guards (cf Clio's telnet.l lines 210–219)
    private bool _ttypeSent;
    private volatile bool _nawsSent;
    private bool _newEnvironSent;

    // Login username advertised via NEW-ENVIRON USER when the server opts in (RFC 1572).
    internal string? LoginUser { get; set; }

    // Configured window size — set before connect; updated on resize; preserved across Reset().
    private int _nawsCols = 80;
    private int _nawsRows = 21;

    // Telnet option codes
    private const byte OPT_ECHO        =  1;
    private const byte OPT_SGA         =  3;
    private const byte OPT_TTYPE       = 24;
    private const byte OPT_NAWS        = 31;
    private const byte OPT_NEW_ENVIRON = 39;

    // Telnet command bytes (https://datatracker.ietf.org/doc/html/rfc854)
    private const byte IAC  = 255; // \377
    private const byte SE   = 240; // \360
    private const byte SB   = 250; // \372
    private const byte WILL = 251; // \373
    private const byte WONT = 252; // \374
    private const byte DO   = 253; // \375
    private const byte DONT = 254; // \376

    // TTYPE sub-negotiation codes (RFC 1091)
    private const byte ENV_IS   = 0;
    private const byte ENV_SEND = 1;

    internal TelnetNegotiator(Action<byte[]> send) => _send = send;

    /// <summary>
    /// Process one byte of the IAC state machine.
    /// Called by MudStreamParser while in any Iac* state.
    /// </summary>
    internal ParserState ProcessByte(byte b, ParserState state, List<byte> sbBuf)
    {
        return state switch
        {
            ParserState.Iac       => HandleIac(b),
            ParserState.IacDo     => HandleDo(b),
            ParserState.IacDont   => HandleDont(b),
            ParserState.IacWill   => HandleWill(b),
            ParserState.IacWont   => HandleWont(b),
            ParserState.IacSb     => HandleSbStart(b, sbBuf),
            ParserState.IacSbData => HandleSbData(b, sbBuf),
            ParserState.IacSbIac  => HandleSbIac(b, sbBuf),
            _                     => ParserState.Normal,
        };
    }

    internal void Reset()
    {
        _ttypeSent      = false;
        _nawsSent       = false;
        _newEnvironSent = false;
        // _nawsCols / _nawsRows intentionally preserved — configured size survives reconnect.
        // LoginUser intentionally preserved — caller re-sets it only on profile change.
    }

    /// <summary>
    /// Update the advertised window size. Sends an updated NAWS subnegotiation immediately
    /// if NAWS has already been negotiated with the server. Thread-safe: called from UI thread.
    /// </summary>
    internal void SetWindowSize(int cols, int rows)
    {
        _nawsCols = cols;
        _nawsRows = rows;
        if (_nawsSent)
            SendNaws((ushort)cols, (ushort)rows);
    }

    // ── IAC dispatch ──────────────────────────────────────────────────────────

    private static ParserState HandleIac(byte b) => b switch
    {
        DO   => ParserState.IacDo,
        DONT => ParserState.IacDont,
        WILL => ParserState.IacWill,
        WONT => ParserState.IacWont,
        SB   => ParserState.IacSb,
        // IAC IAC = escaped literal 0xFF — MUD2 C1 layer handles this separately; consume here
        IAC  => ParserState.Normal,
        _    => ParserState.Normal,
    };

    // ── Option handlers ───────────────────────────────────────────────────────

    /// <summary>Server asked us (client) to DO something.</summary>
    private ParserState HandleDo(byte opt)
    {
        switch (opt)
        {
            case OPT_SGA:
                // Suppress Go-Ahead (WILL SGA on IAC WILL SGA, but the server
                // can also send DO SGA; we agree unconditionally)
                Send(IAC, WILL, OPT_SGA);
                break;

            case OPT_TTYPE:
                // Terminal type one-shot
                if (!_ttypeSent)
                {
                    Send(IAC, WILL, OPT_TTYPE);
                    _ttypeSent = true;
                }
                break;

            case OPT_NAWS:
                // Negotiate About Window Size one-shot WILL, then subnegotiatie
                if (!_nawsSent)
                {
                    _nawsSent = true;   // set before send to narrow the cross-thread race window
                    Send(IAC, WILL, OPT_NAWS);
                    SendNaws((ushort)_nawsCols, (ushort)_nawsRows);
                }
                break;

            case OPT_NEW_ENVIRON:
                // RFC 1572: accept if we have a login user to advertise; refuse otherwise.
                // One-shot: second DO after we've already sent WILL is silently ignored.
                if (!string.IsNullOrEmpty(LoginUser) && !_newEnvironSent)
                {
                    _newEnvironSent = true;
                    Send(IAC, WILL, OPT_NEW_ENVIRON);
                }
                else if (!_newEnvironSent)
                    Send(IAC, WONT, OPT_NEW_ENVIRON);
                break;

            default:
                // Refuse all the other things because we don't support them
                Send(IAC, WONT, opt);
                break;
        }
        return ParserState.Normal;
    }

    /// <summary>Server told us DONT do something — silently accept.</summary>
    private static ParserState HandleDont(byte _) => ParserState.Normal;

    /// <summary>Server offered to WILL do something.</summary>
    private ParserState HandleWill(byte opt)
    {
        switch (opt)
        {
            case OPT_ECHO:
                // Clio telnet.l line 248–249: silently ignore WILL ECHO (break — no response)
                break;

            case OPT_SGA:
                // Suppress Go-Ahead — Clio telnet.l line 250–254
                Send(IAC, DO, OPT_SGA);
                break;

            default:
                // Refuse all other offers — Clio telnet.l line 259–261
                Send(IAC, DONT, opt);
                break;
        }
        return ParserState.Normal;
    }

    /// <summary>Server said WONT do something — silently ignore (Clio telnet.l line 265–267).</summary>
    private static ParserState HandleWont(byte _) => ParserState.Normal;

    // ── Subnegotiation accumulation ───────────────────────────────────────────

    private const int IacSbMaxBufBytes = 1024;

    private static ParserState HandleSbStart(byte b, List<byte> sbBuf)
    {
        sbBuf.Clear();
        sbBuf.Add(b);
        return ParserState.IacSbData;
    }

    private static ParserState HandleSbData(byte b, List<byte> sbBuf)
    {
        if (b == IAC)
            return ParserState.IacSbIac;
        sbBuf.Add(b);
        if (sbBuf.Count > IacSbMaxBufBytes)
        {
            sbBuf.Clear();
            return ParserState.Normal;
        }
        return ParserState.IacSbData;
    }

    private ParserState HandleSbIac(byte b, List<byte> sbBuf)
    {
        if (b == SE)
        {
            HandleSubnegotiation(sbBuf);
            sbBuf.Clear();
            return ParserState.Normal;
        }
        if (b == IAC)
        {
            sbBuf.Add(IAC);
            return ParserState.IacSbData;
        }
        return ParserState.Normal;
    }

    // ── Subnegotiation dispatch ───────────────────────────────────────────────

    private void HandleSubnegotiation(List<byte> buf)
    {
        if (buf.Count < 2) return;

        byte opt  = buf[0];
        byte code = buf[1];

        if (opt == OPT_TTYPE && code == ENV_SEND)
        {
            // Identify ourselves with a valid linux term type that supports
            // ansi, so that mud doesn't think we don't support it.
            Send(IAC, SB, OPT_TTYPE, ENV_IS,
                 (byte)'a', (byte)'n', (byte)'s', (byte)'i',
                 IAC, SE);
        }
        else if (opt == OPT_NEW_ENVIRON && code == ENV_SEND)
        {
            SendNewEnvironIs();
        }
    }

    private void SendNewEnvironIs()
    {
        var user = LoginUser;
        if (string.IsNullOrEmpty(user)) return;
        // IAC SB NEW-ENVIRON IS VAR "USER" VALUE <user> IAC SE  (RFC 1572 §3)
        // VAR=0, VALUE=1 — bytes 0–3 inside values must be ESC-prefixed, but login
        // names are alphanumeric so no escaping is needed in practice.
        var buf = new List<byte> { IAC, SB, OPT_NEW_ENVIRON, ENV_IS,
                                   0,                                          // VAR
                                   (byte)'U', (byte)'S', (byte)'E', (byte)'R',
                                   1 };                                        // VALUE
        foreach (var c in user)
            buf.Add((byte)c);
        buf.Add(IAC);
        buf.Add(SE);
        Send(buf.ToArray());
    }

    // ── Send helpers ──────────────────────────────────────────────────────────

    private void Send(params byte[] bytes) => _send(bytes);

    private void SendNaws(ushort width, ushort height)
    {
        // RFC 854: 0xFF bytes inside subnegotiation payload must be doubled (IAC IAC).
        var payload = new List<byte> { IAC, SB, OPT_NAWS };
        AppendNawsParam(payload, width);
        AppendNawsParam(payload, height);
        payload.Add(IAC);
        payload.Add(SE);
        Send(payload.ToArray());
    }

    private static void AppendNawsParam(List<byte> buf, ushort value)
    {
        byte hi = (byte)(value >> 8);
        byte lo = (byte)(value & 0xFF);
        buf.Add(hi); if (hi == IAC) buf.Add(IAC);
        buf.Add(lo); if (lo == IAC) buf.Add(IAC);
    }
}
