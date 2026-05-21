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
    /// <summary>Fired when the server signals game-mode exit (ESC-C, ESC^F, or "Option" menu text).</summary>
    public event Action? GameModeExited;

    /// <summary>Auto-login credentials to send in response to server prompts.</summary>
    public AutoLoginConfig? AutoLogin { get; set; }

#if DEBUG
    /// <summary>When set, notable stream events (game-mode, FES, telnet) are annotated in the capture.</summary>
    public SessionCapture? Capture { get; set; }
#endif

    private enum State
    {
        Normal,
        Esc,
        EscDash,
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
        GameModeC02BareFF1,
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
        // Prompt-preamble suppression: entered only when {C01}{C255} (0x9C 0xFF 0xFF) is seen alone,
        // which is the unique opener of MUD2's complex prompt sequence.
        PromptPreText,
        PromptPreTextFF1,
        PromptText,
        PromptTextFF1,
    }

    private State _state = State.Normal;
    private byte _iacCmd;
    private readonly StringBuilder _paramBuf = new(32);
    private readonly List<byte> _binaryPayload = new(64);
    private readonly List<byte> _sbPayload = new(32);
    private byte _c99Fg;
    private byte _c99Bg;

    // C1 sequence tracking for color application and prompt detection.
    private byte _c1StartByte;
    private byte _c1SecondByte;

    // Prompt suppression: set when the MUD2 prompt-preamble opener {C01}{C255} is recognised.
    // Text buffered here is discarded if confirmed as prompt content; committed if it turns
    // out to be normal game text (e.g. ended by \n rather than {C255}).
    private bool _inPromptPreamble;
    private bool _inPromptText;
    private readonly StringBuilder _provBuf = new(16);

    private bool _clientModeRequested;
    private bool _accountIdSent;
    private bool _passwordSent;

    // Game-mode state tracking (mirrors clio's `mode` variable)
    private bool _inGameMode;
    // Prompt display: true after each \r\n; first complex prompt resets to false and shows '*'.
    private bool _promptAllowed = true;
    // Set by heartbeat prompts; EmitPartial sends FES when true (after mode-exit check).
    private bool _requestFes;
    // Whether the prompt currently being parsed should be shown (first after \n) or suppressed.
    private bool _showPrompt;
    // Set after a shown '*' prompt; subsequent text on the same line is the player's echoed input.
    // Cleared as soon as any C1/control colour code arrives — those bytes signal game activity text,
    // not player echo (player echo is always plain text with no embedded colour codes).
    private bool _afterShownPrompt;
    // Lines queued here are discarded when received from the server (suppress echo of our own sends).
    private readonly Queue<string> _suppressEchoQueue = new();

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
    {
        if (!_inGameMode) return;
        ResponseReady?.Invoke((byte[])FesSubscriptionRequestBytes.Clone());
    }

    private void ForceRequestFesSubscription()
        => ResponseReady?.Invoke((byte[])FesSubscriptionRequestBytes.Clone());

    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case State.Normal:
                if (b == 0x1B)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.Esc;
                }
                else if (b == IAC)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.IacSeen;
                }
                else if (b == 0x9D)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.GameModePrefix2;
                }
                else if (b == 0xA7)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.FesPrefix2;
                }
                else if (b == 0xAA)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.DreamPrefix2;
                }
                else if (b == 0xFE)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.C99Fg;
                }
                else if (b == 0xFD)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _state = State.C98Data;
                }
                else if (b >= 0x9B && b <= 0xFE)
                {
                    FlushSpan(); _afterShownPrompt = false;
                    _c1StartByte = b;
                    _c1SecondByte = 0;
                    _state = State.C1GenSeq;
                }
                else if (b == '\n')
                {
                    CommitProvisional();
                    _inPromptPreamble = false;
                    _inPromptText = false;
                    FlushSpan();
                    EmitLine();
                }
                else if (b == '\r')
                {
                }
                else if ((b >= 0x20 && b != 0x7F) || b == '\t')
                {
                    if (_inPromptText)
                    {
                        _provBuf.Append((char)b);
                        _state = State.PromptText;
                    }
                    else if (_inPromptPreamble)
                    {
                        _provBuf.Append((char)b);
                        _state = State.PromptPreText;
                    }
                    else
                    {
                        _spanText.Append((char)b);
                    }
                }

                break;

            case State.Esc:
                if (b == '[')
                {
                    _paramBuf.Clear();
                    _state = State.Csi;
                }
                else if (b == '-')
                {
                    _state = State.EscDash;
                }
                else if (b == 0x06)
                {
                    // ESC^F = request client mode; signals game-mode exit
                    ExitGameMode();
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.EscDash:
                // ESC-C = client mode / clear screen (game-mode exit), ESC-R/r = reverse video, ESC-K = erase EOL.
                if (b == (byte)'C') ExitGameMode();
                _state = State.Normal;
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
                    // IAC IAC in MUD2 is never a literal data byte; consume silently.
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
                else if (b == 0xFF)
                {
                    // {C02}{C255}: bare C02 colour sequence = LT_GREEN (same base as game-mode entry)
                    _state = State.GameModeC02BareFF1;
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
                    // {C02}{C01}{C255}: game-mode entry — push LT_GREEN (clio colour stack)
                    FlushSpan();
                    _fg = 10; _bg = 0; _bold = false;
                    var wasInGame = _inGameMode;
                    _inGameMode = true;
                    _promptAllowed = true;
                    _suppressEchoQueue.Clear();     // pre-game echo suppression is done
                    ForceRequestFesSubscription();
                    if (!wasInGame)
                    {
                        GameModeEntered?.Invoke();
#if DEBUG
                        Capture?.Annotate("mode: game-mode entered");
#endif
                    }
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
                    FlushSpan();
                    _fg = 2; _bg = 0; _bold = false;
                    _state = State.Normal;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                break;

            case State.GameModeC02BareFF1:
                if (b == 0xFF)
                {
                    FlushSpan();
                    _fg = 10; _bg = 0; _bold = false;
                }
                else
                {
                    ReprocessFromNormal(b);
                }

                _state = State.Normal;
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
                    _c1SecondByte = b;
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
                    ApplyC1Color();

                    if (_inPromptPreamble)
                    {
                        if (_c1StartByte == 0x9C)
                        {
                            // {C01}{C01/C02/C03}{C255} = prompt type indicator → prompt text follows
                            if (_c1SecondByte == 0x9C || _c1SecondByte == 0x9D || _c1SecondByte == 0x9E)
                            {
                                _inPromptText = true;
                                _inPromptPreamble = false;
                            }
                            // else: {C01}{C04/C05}{C255} = preamble continuation
                        }
                        else
                        {
                            // Non-C01 sequence in preamble: not a prompt, commit any buffered text
                            CommitProvisional();
                            _inPromptPreamble = false;
                            _showPrompt = false;
                        }
                    }
                    else if (_c1StartByte == 0x9C && _c1SecondByte == 0)
                    {
                        // Bare {C01}{C255}: MUD2 prompt-preamble opener
                        if (_inGameMode)
                        {
                            // Show only if this is the first prompt after a \n (_promptAllowed).
                            _showPrompt = _promptAllowed;
                            _promptAllowed = false;
                            _inPromptPreamble = true;
                            if (!_showPrompt) _requestFes = true;
                        }
                        // else: not in game mode — just a colour change (BLUE), no suppression
                    }

                    _state = State.Normal;
                }
                else
                {
                    if (_inPromptPreamble)
                    {
                        CommitProvisional();
                        _inPromptPreamble = false;
                        _showPrompt = false;
                    }

                    ReprocessFromNormal(b);
                }

                break;

            case State.PromptPreText:
                // Buffering preamble text (& or > characters in the prompt prefix).
                if (b == 0xFF)
                {
                    _state = State.PromptPreTextFF1;
                }
                else if (b == '\n')
                {
                    CommitProvisional();
                    _inPromptPreamble = false;
                    _showPrompt = false;
                    FlushSpan();
                    EmitLine();
                    _state = State.Normal;
                }
                else if (b == '\r')
                {
                }
                else if (b >= 0x9B && b <= 0xFE)
                {
                    // C1 byte: could be the type indicator — keep provBuf, process C1.
                    // C1GenFF1 will commit or discard based on what the sequence turns out to be.
                    _c1StartByte = b;
                    _c1SecondByte = 0;
                    _state = State.C1GenSeq;
                }
                else
                {
                    _provBuf.Append((char)b);
                }

                break;

            case State.PromptPreTextFF1:
                if (b == 0xFF)
                {
                    // {C255}: preamble text confirmed (& or >), discard it
                    _provBuf.Clear();
                    _state = State.Normal; // _inPromptPreamble stays true
                }
                else
                {
                    // Real IAC command: commit preamble text as game text and handle it
                    CommitProvisional();
                    _inPromptPreamble = false;
                    _showPrompt = false;
                    FlushSpan();
                    _state = State.IacSeen;
                    ProcessByte(b);
                }

                break;

            case State.PromptText:
                // Buffering the actual prompt text (typically just '*').
                if (b == 0xFF)
                {
                    _state = State.PromptTextFF1;
                }
                else if (b == '\n')
                {
                    // Fallback: prompt text ended with newline — display it
                    CommitProvisional();
                    _inPromptText = false;
                    _showPrompt = false;
                    FlushSpan();
                    EmitLine();
                    _state = State.Normal;
                }
                else if (b == '\r')
                {
                }
                else if (b >= 0x9B && b <= 0xFE)
                {
                    // C1 byte mid-prompt-text: unexpected — surface the text and process C1
                    CommitProvisional();
                    _inPromptText = false;
                    _showPrompt = false;
                    FlushSpan();
                    _c1StartByte = b;
                    _c1SecondByte = 0;
                    _state = State.C1GenSeq;
                }
                else
                {
                    _provBuf.Append((char)b);
                }

                break;

            case State.PromptTextFF1:
                if (b == 0xFF)
                {
                    // {C255}: prompt text confirmed — show if first-after-newline, suppress if heartbeat.
                    if (_showPrompt)
                    {
                        CommitProvisional();    // emit the '*' to _spanText
                        FlushSpan();            // flush '*' as a normal (non-echo) span before arming echo mode
                        _afterShownPrompt = true;
                    }
                    else
                    {
                        _provBuf.Clear();       // suppress heartbeat prompt
                    }
                    _showPrompt = false;
                    _inPromptText = false;
                    _fg = 7; _bg = 0; _bold = false;
                    _state = State.Normal;
                }
                else
                {
                    // Real IAC: surface the text and handle the IAC command
                    CommitProvisional();
                    _showPrompt = false;
                    _inPromptText = false;
                    FlushSpan();
                    _state = State.IacSeen;
                    ProcessByte(b);
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
        _afterShownPrompt = false;
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
#if DEBUG
            Capture?.Annotate($"fes: sta={_stats.Stamina}/{_stats.MaxStamina} str={_stats.Strength} dex={_stats.Dexterity} magic={_stats.Magic} score={_stats.Score} weather={_stats.Weather}");
#endif
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
#if DEBUG
        Capture?.Annotate($"dreamword: set={dreamword}");
#endif
    }

    private void HandleDreamwordCleared()
    {
        _stats.Dreamword = string.Empty;
        StatsUpdated?.Invoke(_stats);
    #if DEBUG
        Capture?.Annotate("dreamword: cleared");
    #endif
        // Do NOT call RequestFesSubscription() here — this fires during the game-exit sequence
        // before mode tracking resets, which causes the server to interpret "FES" as a persona name.
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

    /// <summary>
    /// Applies colour based on the C1 sequence just consumed (_c1StartByte / _c1SecondByte).
    /// Called from C1GenFF1 after the full {Cx...}{C255} terminator is confirmed.
    /// Colour mappings follow clio telnet.l lines 434–810 (ANSI indices).
    /// </summary>
    private void ApplyC1Color()
    {
        FlushSpan();
        switch (_c1StartByte)
        {
            case 0x9B: // C00 = init_stack(WHITE, BLACK)
                _fg = 7; _bg = 0; _bold = false;
                break;
            case 0x9C: // C01 variants
                _bg = 0; _bold = false;
                _fg = _c1SecondByte switch
                {
                    0 => 4,      // {C01}{C255}        = BLUE
                    0x9C => 12,  // {C01}{C01}{C255}   = LT_BLUE
                    0x9D => 12,  // {C01}{C02}{C255}   = LT_BLUE
                    0x9E => 12,  // {C01}{C03}{C255}   = LT_BLUE
                    _ => 4,      // other C01+x        = BLUE
                };
                break;
            case 0x9E: // C03 = cyan (room/location text)
                _fg = _c1SecondByte == 0x9C ? (byte)14 : (byte)6;
                _bg = 0; _bold = false;
                break;
            case 0xA0: // C05 = red (combat/damage)
                _fg = 9; _bg = 0; _bold = false;
                break;
            case 0xA2: // C07 = bright red (important messages)
                _fg = 9; _bg = 0; _bold = true;
                break;
        }
    }

    /// <summary>
    /// Moves any provisional (prompt-candidate) text into the real span buffer,
    /// making it visible as normal game text.
    /// </summary>
    private void CommitProvisional()
    {
        if (_provBuf.Length == 0) return;
        _spanText.Append(_provBuf);
        _provBuf.Clear();
    }

    /// <summary>
    /// Transitions out of game mode (ESC-C, ESC^F, or "Option" menu text detected).
    /// Resets prompt and login state; fires GameModeExited if we were actually in game mode.
    /// </summary>
    private void ExitGameMode()
    {
        if (!_inGameMode) return;
        _inGameMode = false;
        _promptAllowed = true;
        _requestFes = false;
        _afterShownPrompt = false;
        _clientModeRequested = false;   // allow client-mode re-send on next "Option" prompt
        _accountIdSent = false;
        _passwordSent = false;
        _inPromptPreamble = false;
        _inPromptText = false;
        _showPrompt = false;
        _provBuf.Clear();
        GameModeExited?.Invoke();
#if DEBUG
        Capture?.Annotate("mode: game-mode exited");
#endif
    }

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
#if DEBUG
        if (Capture != null)
        {
            var cmdName = cmd switch { WILL => "WILL", WONT => "WONT", DO => "DO", DONT => "DONT", _ => $"0x{cmd:X2}" };
            var optName = option switch
            {
                OPT_ECHO => "ECHO", OPT_SGA => "SGA", OPT_TERMINAL_TYPE => "TERMINAL-TYPE",
                OPT_NAWS => "NAWS", OPT_NEW_ENVIRON => "NEW-ENVIRON", _ => $"0x{option:X2}"
            };
            Capture.Annotate($"telnet: {cmdName} {optName}");
        }
#endif
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
        // Do not commit provisional text while mid-prompt — it hasn't been confirmed as game text yet.
        if (!_inPromptPreamble && !_inPromptText) CommitProvisional();
        FlushSpan();

        // Capture the FES request flag before we potentially clear it below.
        var tryFes = _requestFes;
        _requestFes = false;

        if (_line.Spans.Count > 0)
        {
            var snapshot = new StyledLine { IsPartial = true };
            foreach (var s in _line.Spans) snapshot.Add(s);

            if (!_clientModeRequested)
            {
                var text = snapshot.PlainText;
                if (text.StartsWith("Option:", StringComparison.Ordinal) ||
                    text.StartsWith("Option (H for help):", StringComparison.Ordinal))
                {
                    _clientModeRequested = true;
                    _accountIdSent = false;
                    _passwordSent = false;
                    tryFes = false;     // at login menu — no FES
                    if (_inGameMode)
                    {
                        _inGameMode = false;
                        _promptAllowed = true;
                        _inPromptPreamble = false;
                        _inPromptText = false;
                        _showPrompt = false;
                        _provBuf.Clear();
                        GameModeExited?.Invoke();
                    }
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
                    _suppressEchoQueue.Enqueue(AutoLogin.AccountId);
                    ResponseReady?.Invoke(Encoding.Latin1.GetBytes(AutoLogin.AccountId + "\r\n"));
                }
                else if (!_passwordSent && _accountIdSent && AutoLogin.Password != null
                         && (loginText.StartsWith("Password:", StringComparison.Ordinal)
                             || loginText.StartsWith("password:", StringComparison.Ordinal)))
                {
                    _passwordSent = true;
                    _suppressEchoQueue.Enqueue(AutoLogin.Password);
                    ResponseReady?.Invoke(Encoding.Latin1.GetBytes(AutoLogin.Password + "\r\n"));
                }
            }

            LineReady?.Invoke(snapshot);
        }

        // Send queued FES only after mode-exit checks; ForceRequest bypasses the _inGameMode gate
        // because _requestFes is only set by heartbeat prompts that already require _inGameMode.
        if (tryFes && _inGameMode)
            ForceRequestFesSubscription();
    }

    private void FlushSpan()
    {
        if (_spanText.Length == 0) return;
        byte fg = _bold && _fg < 8 ? (byte)(_fg | 8) : _fg;
        _line.Add(new StyledSpan { Text = _spanText.ToString(), Fg = fg, Bg = _bg, Bold = _bold, Echo = _afterShownPrompt });
        _spanText.Clear();
    }

    private void EmitLine()
    {
        FlushSpan();
        _promptAllowed = true;
        _afterShownPrompt = false;
        var text = string.Concat(_line.Spans.Select(static span => span.Text));
        if (_suppressEchoQueue.Count > 0 && _suppressEchoQueue.Peek() == text)
        {
            _suppressEchoQueue.Dequeue();
            _line = new StyledLine();
            return;
        }
        CheckOutOfBandStamina(text);
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
