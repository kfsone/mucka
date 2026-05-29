using System.Collections.Generic;
using System.Text;
using MudSharp.Models;

namespace MudSharp.Protocol;

internal sealed class AnsiSgrState
{
    public TextStyle CurrentStyle { get; private set; } = TextStyle.Default;

    /// <summary>
    /// Invoked when the server confirms the terminal width via ESC-<n>W.
    /// Payload is the confirmed width value.
    /// </summary>
    internal Action<int>? WidthConfirmed;

    // Campbell color palette hex values for HTML/UI consumers.
    public static readonly IReadOnlyDictionary<AnsiColor, string> CampbellHex =
        new Dictionary<AnsiColor, string>
        {
            [AnsiColor.Black]         = "#0C0C0C",
            [AnsiColor.Red]           = "#C50F1F",
            [AnsiColor.Green]         = "#13A10E",
            [AnsiColor.Yellow]        = "#C19C00",
            [AnsiColor.Blue]          = "#0037DA",
            [AnsiColor.Magenta]       = "#881798",
            [AnsiColor.Cyan]          = "#3A96DD",
            [AnsiColor.White]         = "#CCCCCC",
            [AnsiColor.BrightBlack]   = "#767676",
            [AnsiColor.BrightRed]     = "#E74856",
            [AnsiColor.BrightGreen]   = "#16C60C",
            [AnsiColor.BrightYellow]  = "#F9F1A5",
            [AnsiColor.BrightBlue]    = "#3B78FF",
            [AnsiColor.BrightMagenta] = "#B4009E",
            [AnsiColor.BrightCyan]    = "#61D6D6",
            [AnsiColor.BrightWhite]   = "#F2F2F2",
        };

    private readonly StringBuilder _paramBuf = new(32);

    /// <summary>
    /// Feed a single byte to the SGR state machine.
    /// <paramref name="state"/> is the parser's current state (what triggered this call).
    /// Returns the next <see cref="ParserState"/> the outer parser should enter.
    /// </summary>
    internal ParserState ProcessByte(byte b, ParserState state)
    {
        switch (state)
        {
            case ParserState.Escape:
                // Byte received immediately after ESC.
                if (b == (byte)'[')
                {
                    _paramBuf.Clear();
                    return ParserState.EscapeBracket;
                }
                if (b == 0x2D) return ParserState.EscapeDash; // ESC - (MUD2 shell command)
                // Any other byte after ESC is unrecognised — consume and return to Normal.
                return ParserState.Normal;

            case ParserState.EscapeDash:
                // A digit starts the server's terminal-width confirmation (ESC-<n>W).
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    _paramBuf.Clear();
                    _paramBuf.Append((char)b);
                    return ParserState.EscapeDashWidth;
                }
                // Any other byte is a named shell command letter — consume it silently.
                return ParserState.Normal;

            case ParserState.EscapeDashWidth:
                // Accumulate additional digits.
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    _paramBuf.Append((char)b);
                    return ParserState.EscapeDashWidth;
                }
                // 'W' = server confirms the new terminal width.
                if (b == (byte)'W')
                {
                    if (int.TryParse(_paramBuf.ToString(), out int w))
                        WidthConfirmed?.Invoke(w);
                    _paramBuf.Clear();
                    return ParserState.EscapeDashAnnotation;
                }
                // Any other terminator — discard collected digits.
                _paramBuf.Clear();
                return ParserState.Normal;

            case ParserState.EscapeBracket:
            case ParserState.CsiParam:
                if (b == (byte)'m')
                {
                    ApplySgr(_paramBuf.ToString());
                    _paramBuf.Clear();
                    return ParserState.Normal;
                }
                if (b is >= (byte)'0' and <= (byte)'9' || b == (byte)';')
                {
                    _paramBuf.Append((char)b);
                    return ParserState.CsiParam;
                }
                // Unrecognised terminator — discard the sequence.
                _paramBuf.Clear();
                return ParserState.Normal;

            default:
                return ParserState.Normal;
        }
    }

    /// <summary>Directly sets the current style (called by C1 color codes).</summary>
    internal void SetStyle(TextStyle style) => CurrentStyle = style;

    internal void Reset()
    {
        _paramBuf.Clear();
        CurrentStyle = TextStyle.Default;
    }

    private void ApplySgr(string paramStr)
    {
        // Empty params (ESC[m) are equivalent to a single "0" (reset).
        var parts = paramStr.Length == 0
            ? (string[])["0"]
            : paramStr.Split(';');

        var style = CurrentStyle;
        foreach (var part in parts)
        {
            // Bare semicolons produce empty strings — treat as 0.
            int n = part.Length == 0 ? 0 : -1;
            if (n < 0 && !int.TryParse(part, out n))
                continue;

            style = n switch
            {
                0                  => TextStyle.Default,
                1                  => style with { Bold      = true  },
                4                  => style with { Underline = true  },
                5                  => style with { Blink     = true  },
                7                  => style with { Reverse   = true  },
                22                 => style with { Bold      = false },
                24                 => style with { Underline = false },
                25                 => style with { Blink     = false },
                27                 => style with { Reverse   = false },
                >= 30 and <= 37    => style with { Foreground = (AnsiColor)(n - 30)       },
                38                 => style,   // extended color — not used in MUD2
                39                 => style with { Foreground = AnsiColor.Default          },
                >= 40 and <= 47    => style with { Background = (AnsiColor)(n - 40)       },
                49                 => style with { Background = AnsiColor.Default          },
                >= 90 and <= 97    => style with { Foreground = (AnsiColor)(n - 90 + 8)   },
                >= 100 and <= 107  => style with { Background = (AnsiColor)(n - 100 + 8)  },
                _                  => style,
            };
        }

        CurrentStyle = style;
    }
}
