using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mucka.Core;

/// <summary>
/// Parses a raw MUD2 telnet byte stream into styled lines and game-stat events.
///
/// Input bytes arrive via Feed(). The parser:
///  - strips IAC telnet negotiation sequences and fires ResponseReady with reply bytes
///  - processes ANSI SGR escape codes into colour/style attributes
///  - fires LineReady for each complete display line (on \n)
///  - fires StatsUpdated when MUD2 FES or dreamword packets are received
/// </summary>
public sealed class MudStream
{
    private static readonly byte[] FesSubscriptionRequestBytes = [0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D];
    // ESC^F = request client mode; ESC-T = text mode 78x21 (matches Clio's autoclient request)
    private static readonly byte[] ClientModeRequestBytes = [0x1B, 0x06, 0x1B, 0x2D, 0x54];
    private static readonly Regex StaminaMaxPattern = new("^stamina:\\s*(\\d+)\\s+max:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StaminaOnlyPattern = new("^Your stamina is (\\d+)\\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CompactStaminaPattern = new("^\\((\\d+)/(\\d+)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public event Action<StyledLine>? LineReady;
    public event Action<GameStats>? StatsUpdated;
    public event Action<byte[]>? ResponseReady;
    /// <summary>Fired when the server sends the C02·C01·C255 game-mode entry signal.</summary>
    public event Action? GameModeEntered;

    /// <summary>Auto-login credentials to send in response to server prompts.</summary>
    public AutoLoginConfig? AutoLogin { get; set; }

    private enum State
    {
        Normal,
        Esc,
        Csi,
        IacSeen,
        IacCmd,
        IacSb,
        IacSbIac,
        FesPrefix2,
        FesPrefix3,
        FesPrefix4,
        FesPrefix5,
        FesPayload,
        DreamPrefix2,
        DreamPrefix3,
        DreamWordPrefix4,
        DreamWordPrefix5,
        DreamWordPayload,
        DreamClearPrefix4,
        DreamClearPrefix5,
        DreamC15C00FF1,
        GameModePrefix2,
        GameModePrefix3,
        GameModePrefix4,
        GameModeC02C02FF1,
        GameModeC02C02FF2,
        C99Fg,
        C99FgBgOrTerm,
        C99Term1,
        C99BgTerm,
        C99BgTerm1,
        C98Data,
        C98DataFF1,
        C98DataFF2,
        C1GenSeq,
        C1GenFF1,
    }

    private State _state = State.Normal;
    private byte _iacCmd;
    private readonly StringBuilder _paramBuf = new(32);
    private readonly List<byte> _binaryPayload = new(64);
    private readonly List<byte> _sbPayload = new(32);
    private byte _c99Fg;
    private byte _c99Bg;

    private bool _clientModeRequested;
    private bool _accountIdSent;
    private bool _passwordSent;

    private byte _fg = 7;
    private byte _bg = 0;
    private bool _bold;

    private StyledLine _line = new();
    private readonly StringBuilder _spanText = new(80);

    private readonly GameStats _stats = new();

    private const byte IAC = 0xFF;
    private const byte SE = 0xF0;
    private const byte SB = 0xFA;
    private const byte WILL = 0xFB;
    private const byte WONT = 0xFC;
    private const byte DO = 0xFD;
    private const byte DONT = 0xFE;
    private const byte OPT_SGA = 0x03;
    private const byte OPT_ECHO = 0x01;
    private const byte OPT_TERMINAL_TYPE = 0x18;
    private const byte OPT_NAWS = 0x1F;
    private const byte OPT_NEW_ENVIRON = 0x27;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            ProcessByte(b);
        }
    }

    public void RequestFesSubscription()
        => ResponseReady?.Invoke((byte[])FesSubscriptionRequestBytes.Clone());

    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case State.Normal:
                if (b == 0x1B)
                {
                    FlushSpan();
                    _state = State.Esc;
                }
                else if (b == IAC)
                {
                    FlushSpan();
                    _state = State.IacSeen;
                }
                else if (b == 0x9D)
                {
                    FlushSpan();
                    _state = State.GameModePrefix2;
                }
                else if (b == 0xA7)
                {
                    FlushSpan();
                    _state = State.FesPrefix2;
                }
                else if (b == 0xAA)
                {
                    FlushSpan();
                    _state = State.DreamPrefix2;
                }
                else if (b == 0xFE)
                {
                    FlushSpan();
                    _state = State.C99Fg;
                }
                else if (b == 0xFD)
                {
                    FlushSpan();
                    _state = State.C98Data;
                }
                else if (b >= 0x9B && b <= 0xFE)
                {
                    FlushSpan();
                    _state = State.C1GenSeq;
                }
                else if (b == '\n')
                {
                    FlushSpan();
                    EmitLine();
                }
                else if (b == '\r')
                {
                }
                else if ((b >= 0x20 && b != 0x7F) || b == '\t')
                {
                    _spanText.Append((char)b);
                }

                break;

            case State.Esc:
                if (b == '[')
                {
                    _paramBuf.Clear();
                    _state = State.Csi;
                }
                else
                {
                    _state = State.Normal;
                }

                break;

            case State.Csi:
                if (b == ';' || (b >= '0' && b <= '9'))
                {
                    _paramBuf.Append((char)b);
                }
                else if (b >= 0x40 && b <= 0x7E)
                {
                    if (b == 'm')
                    {
                        HandleSgr(_paramBuf.ToString());
                    }

                    _state = State.Normal;
                }

                break;

            case State.IacSeen:
                if (b == WILL || b == WONT || b == DO || b == DONT)
                {
                    _iacCmd = b;
                    _state = State.IacCmd;
                }
                else if (b == SB)
                {
                    _sbPayload.Clear();
                    _state = State.IacSb;
                }
                else if (b == IAC)
                {
                    _spanText.Append((char)0xFF);
                    _state = State.Normal;
                }
                else
                {
                    _state = State.Normal;
                }

                break;

            case State.IacCmd:
                NegotiateResponse(_iacCmd, b);
                _state = State.Normal;
                break;

            case State.IacSb:
                if (b == IAC)
                {
                    _state = State.IacSbIac;
                }
                else
                {
                    _sbPayload.Add(b);
                }

                break;

            case State.IacSbIac:
                if (b == SE)
                {
                    HandleSubNegotiation();
                    _state = State.Normal;
                }
                else
                {
                    _sbPayload.Add(IAC);
                    _sbPayload.Add(b);
                    _state = State.IacSb;
                }

                break;

            case State.FesPrefix2:
                MatchOrReprocess(b, 0xA3, State.FesPrefix3);
                break;

            case State.FesPrefix3:
                MatchOrReprocess(b, 0x9C, State.FesPrefix4);
                break;

            case State.FesPrefix4:
                MatchOrReprocess(b, 0xFF, State.FesPrefix5);
                break;

            case State.FesPrefix5:
                if (b == 0xFF)
                {
                    _binaryPayload.Clear();
                    _state = State.FesPayload;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.FesPayload:
                if (b == '\n')
                {
                    ParseFesPayload();
                    _binaryPayload.Clear();
                    _state = State.Normal;
                }
                else if (b != '\r')
                {
                    _binaryPayload.Add(b);
                }

                break;

            case State.DreamPrefix2:
                MatchOrReprocess(b, 0x9B, State.DreamPrefix3);
                break;

            case State.DreamPrefix3:
                if (b == 0x9B)
                {
                    _state = State.DreamWordPrefix4;
                }
                else if (b == 0x9C)
                {
                    _state = State.DreamClearPrefix4;
                }
                else if (b == 0xFF)
                {
                    _state = State.DreamC15C00FF1;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.DreamWordPrefix4:
                MatchOrReprocess(b, 0xFF, State.DreamWordPrefix5);
                break;

            case State.DreamWordPrefix5:
                if (b == 0xFF)
                {
                    _binaryPayload.Clear();
                    _state = State.DreamWordPayload;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.DreamWordPayload:
                if (b >= 'a' && b <= 'z' && _binaryPayload.Count < 14)
                {
                    _binaryPayload.Add(b);
                }
                else
                {
                    ParseDreamwordPayload();
                    _binaryPayload.Clear();
                    ReprocessFromNormal(b);
                }

                break;

            case State.DreamClearPrefix4:
                MatchOrReprocess(b, 0xFF, State.DreamClearPrefix5);
                break;

            case State.DreamClearPrefix5:
                if (b == 0xFF)
                {
                    HandleDreamwordCleared();
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.DreamC15C00FF1:
                if (b == 0xFF)
                {
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.GameModePrefix2:
                if (b == 0x9C)
                {
                    _state = State.GameModePrefix3;
                }
                else if (b == 0x9D)
                {
                    _state = State.GameModeC02C02FF1;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.GameModePrefix3:
                MatchOrReprocess(b, 0xFF, State.GameModePrefix4);
                break;

            case State.GameModePrefix4:
                if (b == 0xFF)
                {
                    RequestFesSubscription();
                    GameModeEntered?.Invoke();
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.GameModeC02C02FF1:
                if (b == 0xFF)
                {
                    _state = State.GameModeC02C02FF2;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.GameModeC02C02FF2:
                if (b == 0xFF)
                {
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.C99Fg:
                _c99Fg = b;
                _state = State.C99FgBgOrTerm;
                break;

            case State.C99FgBgOrTerm:
                if (b == 0xFF)
                {
                    _state = State.C99Term1;
                }
                else
                {
                    _c99Bg = b;
                    _state = State.C99BgTerm;
                }

                break;

            case State.C99Term1:
                if (b == 0xFF)
                {
                    if (_c99Fg == 0xFE)
                    {
                        _fg = 7;
                        _bg = 0;
                    }
                    else
                    {
                        _fg = ClampColorIndex(_c99Fg - 0x9B);
                        _bg = 0;
                    }
                }

                _state = State.Normal;
                break;

            case State.C99BgTerm:
                if (b == 0xFF)
                    _state = State.C99BgTerm1;
                else
                    ReprocessFromNormal(b);
                break;

            case State.C99BgTerm1:
                if (b == 0xFF)
                {
                    _fg = ClampColorIndex(_c99Fg - 0x9B);
                    _bg = ClampColorIndex(_c99Bg - 0x9B);
                }

                _state = State.Normal;
                break;

            case State.C98Data:
                _state = State.C98DataFF1;
                break;

            case State.C98DataFF1:
                if (b == 0xFF)
                    _state = State.C98DataFF2;
                else
                    ReprocessFromNormal(b);
                break;

            case State.C98DataFF2:
                _state = State.Normal;
                break;

            case State.C1GenSeq:
                if (b >= 0x9B && b <= 0xFE)
                {
                    _state = State.C1GenSeq;
                }
                else if (b == 0xFF)
                {
                    _state = State.C1GenFF1;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.C1GenFF1:
                if (b == 0xFF)
                {
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;
        }
    }

    private void MatchOrReprocess(byte actual, byte expected, State nextState)
    {
        if (actual == expected)
        {
            _state = nextState;
            return;
        }

        ReprocessFromNormal(actual);
    }

    private void ReprocessFromNormal(byte b)
    {
        _state = State.Normal;
        ProcessByte(b);
    }

    private void HandleSgr(string param)
    {
        FlushSpan();
        var parts = param.Length == 0 ? new[] { "0" } : param.Split(';');
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var n))
            {
                continue;
            }

            switch (n)
            {
                case 0:
                    _fg = 7;
                    _bg = 0;
                    _bold = false;
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case >= 30 and <= 37:
                    _fg = (byte)(n - 30);
                    break;
                case 39:
                    _fg = 7;
                    break;
                case >= 40 and <= 47:
                    _bg = (byte)(n - 40);
                    break;
                case 49:
                    _bg = 0;
                    break;
                case >= 90 and <= 97:
                    _fg = (byte)(n - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _bg = (byte)(n - 100 + 8);
                    break;
            }
        }
    }

    private void ParseFesPayload()
    {
        if (_binaryPayload.Count == 0)
        {
            return;
        }

        var textBytes = new List<byte>(_binaryPayload.Count);
        byte? staminaColour = null;

        for (var i = 0; i < _binaryPayload.Count; i++)
        {
            var b = _binaryPayload[i];
            if (b == 0xFE && i + 1 < _binaryPayload.Count)
            {
                var colourIndex = _binaryPayload[i + 1] - 0x9B;
                if (colourIndex is >= 0 and <= 15)
                {
                    staminaColour = (byte)colourIndex;
                }

                i++;
                continue;
            }

            textBytes.Add(b);
        }

        var payloadText = Encoding.ASCII.GetString(textBytes.ToArray());
        var fields = payloadText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 15)
        {
            return;
        }

        var updated = false;

        updated |= TrySetInt(fields[0], value => _stats.Stamina = value);
        updated |= TrySetInt(fields[1], value => _stats.MaxStamina = value);
        updated |= TrySetInt(fields[2], value => _stats.Strength = value);
        updated |= TrySetInt(fields[3], value => _stats.MaxStrength = value);
        updated |= TrySetInt(fields[4], value => _stats.Dexterity = value);
        updated |= TrySetInt(fields[5], value => _stats.MaxDexterity = value);
        updated |= TrySetInt(fields[6], value => _stats.Magic = value);
        updated |= TrySetInt(fields[7], value => _stats.MaxMagic = value);
        updated |= TrySetLong(fields[8], value => _stats.Score = value);
        updated |= TrySetBool(fields[9], value => _stats.Blind = value);
        updated |= TrySetBool(fields[10], value => _stats.Deaf = value);
        updated |= TrySetBool(fields[11], value => _stats.Crippled = value);
        updated |= TrySetBool(fields[12], value => _stats.Dumb = value);
        updated |= TrySetInt(fields[13], value => _stats.MinutesToReset = value);
        updated |= TrySetWeather(fields[14], value => _stats.Weather = value);

        if (staminaColour.HasValue)
        {
            _stats.StaminaColour = staminaColour.Value;
            updated = true;
        }

        // TODO: Rank is not part of the FES payload; keep populating it from a separate source.
        if (updated)
        {
            StatsUpdated?.Invoke(_stats);
        }
    }

    private void ParseDreamwordPayload()
    {
        var dreamword = Encoding.ASCII.GetString(_binaryPayload.ToArray()).Trim();
        if (dreamword.Length is < 1 or > 14)
        {
            return;
        }

        foreach (var ch in dreamword)
        {
            if (ch is < 'a' or > 'z')
            {
                return;
            }
        }

        _stats.Dreamword = dreamword;
        StatsUpdated?.Invoke(_stats);
    }

    private void HandleDreamwordCleared()
    {
        _stats.Dreamword = string.Empty;
        StatsUpdated?.Invoke(_stats);
        RequestFesSubscription();
    }

    private static bool TrySetInt(string text, Action<int> setter)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        setter(value);
        return true;
    }

    private static bool TrySetLong(string text, Action<long> setter)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        setter(value);
        return true;
    }

    private static bool TrySetBool(string text, Action<bool> setter)
    {
        if (text == "Y")
        {
            setter(true);
            return true;
        }

        if (text == "N")
        {
            setter(false);
            return true;
        }

        return false;
    }

    private static bool TrySetWeather(string text, Action<char> setter)
    {
        if (text.Length != 1)
        {
            return false;
        }

        setter(text[0]);
        return true;
    }

    private void HandleSubNegotiation()
    {
        if (_sbPayload.Count < 2) return;
        if (_sbPayload[0] == OPT_TERMINAL_TYPE && _sbPayload[1] == 0x01)
        {
            byte[] resp = [IAC, SB, OPT_TERMINAL_TYPE, 0x00, (byte)'a', (byte)'n', (byte)'s', (byte)'i', IAC, SE];
            ResponseReady?.Invoke(resp);
        }
        else if (_sbPayload[0] == OPT_NEW_ENVIRON && _sbPayload[1] == 0x01)
        {
            SendNewEnvironIs();
        }
    }

    private static byte ClampColorIndex(int colorIndex)
        => (byte)Math.Clamp(colorIndex, 0, 15);

    private void SendNewEnvironIs()
    {
        var user = AutoLogin?.EnvUser;
        if (string.IsNullOrEmpty(user)) return;
        // IAC SB NEW-ENVIRON IS VAR "USER" VALUE {user} IAC SE
        // IS=0x00, VAR=0x00, VALUE=0x01
        var userBytes = Encoding.Latin1.GetBytes(user);
        byte[] preamble = [IAC, SB, OPT_NEW_ENVIRON, 0x00, 0x00, (byte)'U', (byte)'S', (byte)'E', (byte)'R', 0x01];
        byte[] trailer = [IAC, SE];
        var resp = new byte[preamble.Length + userBytes.Length + trailer.Length];
        preamble.CopyTo(resp, 0);
        userBytes.CopyTo(resp, preamble.Length);
        trailer.CopyTo(resp, preamble.Length + userBytes.Length);
        ResponseReady?.Invoke(resp);
    }

    private void NegotiateResponse(byte cmd, byte option)
    {
        switch (cmd)
        {
            case WILL:
                var resp = option == OPT_SGA || option == OPT_ECHO ? DO : DONT;
                ResponseReady?.Invoke([IAC, resp, option]);
                break;
            case DO when option == OPT_NAWS:
                ResponseReady?.Invoke([IAC, WILL, OPT_NAWS]);
                ResponseReady?.Invoke([IAC, SB, OPT_NAWS, 0, 80, 0, 21, IAC, SE]);
                break;
            case DO when option == OPT_TERMINAL_TYPE:
                ResponseReady?.Invoke([IAC, WILL, OPT_TERMINAL_TYPE]);
                break;
            case DO when option == OPT_NEW_ENVIRON:
                ResponseReady?.Invoke([IAC, WILL, OPT_NEW_ENVIRON]);
                SendNewEnvironIs();
                break;
            case DO:
                ResponseReady?.Invoke([IAC, WONT, option]);
                break;
        }
    }

    /// <summary>
    /// Emit whatever partial line is currently buffered (e.g. a login prompt with no trailing newline).
    /// Called by MudConnection after every Feed() so prompts appear immediately.
    /// The internal _line is NOT reset; bytes continue accumulating until \n arrives.
    /// </summary>
    public void EmitPartial()
    {
        FlushSpan();
        if (_line.Spans.Count == 0) return;

        var snapshot = new StyledLine { IsPartial = true };
        foreach (var s in _line.Spans) snapshot.Add(s);

        if (!_clientModeRequested)
        {
            var text = snapshot.PlainText;
            if (text.StartsWith("Option:", StringComparison.Ordinal) ||
                text.StartsWith("Option (H for help):", StringComparison.Ordinal))
            {
                _clientModeRequested = true;
                ResponseReady?.Invoke((byte[])ClientModeRequestBytes.Clone());
            }
        }

        if (AutoLogin != null)
        {
            var loginText = snapshot.PlainText.TrimStart('\r');
            if (!_accountIdSent && AutoLogin.AccountId != null
                && loginText.StartsWith("Account ID:", StringComparison.Ordinal))
            {
                _accountIdSent = true;
                ResponseReady?.Invoke(Encoding.Latin1.GetBytes(AutoLogin.AccountId + "\r\n"));
            }
            else if (!_passwordSent && _accountIdSent && AutoLogin.Password != null
                     && (loginText.StartsWith("Password:", StringComparison.Ordinal)
                         || loginText.StartsWith("password:", StringComparison.Ordinal)))
            {
                _passwordSent = true;
                ResponseReady?.Invoke(Encoding.Latin1.GetBytes(AutoLogin.Password + "\r\n"));
            }
        }

        LineReady?.Invoke(snapshot);
    }

    private void FlushSpan()
    {
        if (_spanText.Length == 0) return;
        byte fg = _bold && _fg < 8 ? (byte)(_fg | 8) : _fg;
        _line.Add(new StyledSpan { Text = _spanText.ToString(), Fg = fg, Bg = _bg, Bold = _bold });
        _spanText.Clear();
    }

    private void EmitLine()
    {
        FlushSpan();
        CheckOutOfBandStamina(string.Concat(_line.Spans.Select(static span => span.Text)));
        LineReady?.Invoke(_line);
        _line = new StyledLine();
    }

    private void CheckOutOfBandStamina(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return;
        }

        var match = StaminaMaxPattern.Match(plainText);
        if (match.Success)
        {
            _stats.Stamina = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            _stats.MaxStamina = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            StatsUpdated?.Invoke(_stats);
            return;
        }

        match = StaminaOnlyPattern.Match(plainText);
        if (match.Success)
        {
            _stats.Stamina = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            StatsUpdated?.Invoke(_stats);
            return;
        }

        match = CompactStaminaPattern.Match(plainText);
        if (match.Success)
        {
            _stats.Stamina = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            _stats.MaxStamina = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            StatsUpdated?.Invoke(_stats);
        }
    }
}
