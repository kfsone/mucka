using System.Text;

namespace Mucka.Core;

/// <summary>
/// Parses a raw MUD2 telnet byte stream into styled lines and game-stat events.
///
/// Input bytes arrive via Feed(). The parser:
///  - strips IAC telnet negotiation sequences and fires ResponseReady with reply bytes
///  - processes ANSI SGR escape codes into colour/style attributes
///  - fires LineReady for each complete display line (on \n)
///  - fires StatsUpdated when a MUD2 FES stats sequence is received
/// </summary>
public sealed class MudStream
{
    public event Action<StyledLine>? LineReady;
    public event Action<GameStats>? StatsUpdated;
    public event Action<byte[]>? ResponseReady;

    private enum State
    {
        Normal,
        Esc,
        Csi,
        IacSeen,
        IacCmd,
        IacSb,
        IacSbIac,
    }

    private State _state = State.Normal;
    private byte _iacCmd;
    private bool _csiPrivate;
    private readonly StringBuilder _paramBuf = new(32);

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

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            ProcessByte(b);
        }
    }

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
                    _csiPrivate = false;
                    _state = State.Csi;
                }
                else
                {
                    _state = State.Normal;
                }

                break;

            case State.Csi:
                if (b == '?')
                {
                    _csiPrivate = true;
                }
                else if (b == ';' || (b >= '0' && b <= '9'))
                {
                    _paramBuf.Append((char)b);
                }
                else if (b >= 0x40 && b <= 0x7E)
                {
                    if (!_csiPrivate && b == 'm')
                    {
                        HandleSgr(_paramBuf.ToString());
                    }
                    else if (_csiPrivate)
                    {
                        HandleFes(_paramBuf.ToString());
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

                break;

            case State.IacSbIac:
                _state = b == SE ? State.Normal : State.IacSb;
                break;
        }
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

    private void HandleFes(string param)
    {
        var f = param.Split(';');
        var updated = false;

        if (f.Length >= 1 && int.TryParse(f[0], out var sta))
        {
            _stats.Stamina = sta;
            updated = true;
        }

        if (f.Length >= 2 && int.TryParse(f[1], out var msta))
        {
            _stats.MaxStamina = msta;
            updated = true;
        }

        if (f.Length >= 3 && int.TryParse(f[2], out var str))
        {
            _stats.Strength = str;
            updated = true;
        }

        if (f.Length >= 4 && int.TryParse(f[3], out var dex))
        {
            _stats.Dexterity = dex;
            updated = true;
        }

        if (f.Length >= 5 && long.TryParse(f[4], out var sc))
        {
            _stats.Score = sc;
            updated = true;
        }

        if (f.Length >= 6 && !string.IsNullOrEmpty(f[5]))
        {
            _stats.Rank = f[5];
            updated = true;
        }

        if (f.Length >= 7 && !string.IsNullOrEmpty(f[6]))
        {
            _stats.Dreamword = f[6];
            updated = true;
        }

        if (updated)
        {
            StatsUpdated?.Invoke(_stats);
        }
    }

    private void NegotiateResponse(byte cmd, byte option)
    {
        var resp = cmd switch
        {
            WILL => option == OPT_SGA || option == OPT_ECHO ? DO : DONT,
            WONT => DONT,
            DO => WONT,
            DONT => WONT,
            _ => WONT,
        };
        ResponseReady?.Invoke(new[] { IAC, resp, option });
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

        // Snapshot the current spans into a new partial StyledLine.
        var snapshot = new StyledLine { IsPartial = true };
        foreach (var s in _line.Spans) snapshot.Add(s);
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
        LineReady?.Invoke(_line);   // IsPartial == false (default)
        _line = new StyledLine();
    }
}
