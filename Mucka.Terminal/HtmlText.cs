using System.Globalization;
using System.Net;
using System.Text;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// A line of screen as HTML, so a log opened in a browser reads as the pane did: each span in the
/// pane's own colour (<see cref="CampbellPalette.ForegroundRgb"/>, the player's chat colours among
/// them) and face, on the pane's page colour.
///
/// <para>A span in the pane's default text colour (<see cref="CampbellPalette.DefaultForegroundSlot"/>)
/// gets no colour, and a span on its page colour (<see cref="CampbellPalette.BackgroundSlot"/>) no
/// background: the page draws those. Bold and italic are <see cref="ChatColorizer.Palette.FaceOf"/>'s,
/// as in <see cref="SgrText"/>, plus underline; reverse and blink are not written, as the pane draws
/// neither. Neighbouring spans of one look share one element, and default text has none.</para>
///
/// <para>Everything outside ASCII, and HTML's own markup characters, are written as character
/// references, so the page is ASCII whatever the server sent. Control characters other than a tab are
/// dropped, the C1 range among them: HTML has no use for them, and a browser reads a reference to one
/// as a Windows-1252 character instead.</para>
///
/// <para>Bold default text is drawn bright, as the pane draws it, so it carries a colour.</para>
/// </summary>
public static class HtmlText
{
    /// <summary><paramref name="line"/> as HTML, its chat spans drawn from <paramref name="chat"/>.
    /// A line with no text is empty.</summary>
    public static string Format(StyledLine line, ChatColorizer.Palette chat)
    {
        ArgumentNullException.ThrowIfNull(line);
        var html = new StringBuilder();
        string? open = null;
        foreach (var span in line.Spans)
        {
            if (span.Text.Length == 0)
                continue;
            var style = StyleOf(span.Style, chat);
            if (style != open)
            {
                if (open is not null)
                    html.Append("</span>");
                if (style is not null)
                    html.Append("<span style=\"").Append(style).Append("\">");
                open = style;
            }
            html.Append(Encode(span.Text));
        }
        if (open is not null)
            html.Append("</span>");
        return html.ToString();
    }

    /// <summary><paramref name="text"/> as HTML text: markup characters and everything outside ASCII as
    /// character references, control characters (C0, DEL and C1) other than a tab dropped.</summary>
    public static string Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
            if ((c >= ' ' && c is < (char)0x7F or > (char)0x9F) || c == '\t')
                kept.Append(c);
        var encoded = WebUtility.HtmlEncode(kept.ToString());
        // HtmlEncode leaves some characters outside Latin-1 as they are; the page is ASCII.
        if (encoded.All(char.IsAscii))
            return encoded;
        var ascii = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (char.IsAscii(c))
            {
                ascii.Append(c);
                continue;
            }
            int code = c;
            if (char.IsHighSurrogate(c) && i + 1 < encoded.Length && char.IsLowSurrogate(encoded[i + 1]))
                code = char.ConvertToUtf32(c, encoded[++i]);
            else if (char.IsSurrogate(c))
                code = 0xFFFD;   // half a pair is no character
            ascii.Append("&#").Append(code.ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        return ascii.ToString();
    }

    /// <summary>The page's opening, through the <c>pre</c> the lines go in: the pane's page and default
    /// text colours, a monospace face, and long lines wrapped at the window's edge as the pane wraps
    /// them.</summary>
    public static string Head(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="Mucka">
            <title>{{Encode(title)}}</title>
            <style>
            body { margin: 0; background: {{Hex(CampbellPalette.Rgb[CampbellPalette.BackgroundSlot])}}; color: {{Hex(CampbellPalette.Rgb[CampbellPalette.DefaultForegroundSlot])}}; }
            pre { margin: 0; padding: 12px 16px; font-family: Consolas, Menlo, "DejaVu Sans Mono", monospace; font-size: 14px; line-height: 1.35; white-space: pre-wrap; overflow-wrap: anywhere; }
            </style>
            </head>
            <body><pre>
            """;
    }

    /// <summary>What closes the page <see cref="Head"/> opened.</summary>
    public const string Foot = "</pre></body></html>";

    private static string? StyleOf(TextStyle style, ChatColorizer.Palette chat)
    {
        var parts = new List<string>(5);
        var foreground = CampbellPalette.ForegroundRgb(style, chat);
        if (foreground != CampbellPalette.Rgb[CampbellPalette.DefaultForegroundSlot])
            parts.Add("color:" + Hex(foreground));
        var background = (int)style.Background;
        if (background is >= 0 and < 16 && background != CampbellPalette.BackgroundSlot)
            parts.Add("background:" + Hex(CampbellPalette.Rgb[background]));
        var face = chat.FaceOf(style);
        if ((face & ChatFace.Bold) != 0)
            parts.Add("font-weight:bold");
        if ((face & ChatFace.Italic) != 0)
            parts.Add("font-style:italic");
        if (style.Underline)
            parts.Add("text-decoration:underline");
        return parts.Count == 0 ? null : string.Join(';', parts);
    }

    private static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}

/// <summary>
/// The writer an HTML log goes through: <see cref="HtmlText.Head"/> before the first line and
/// <see cref="HtmlText.Foot"/> when it is closed, so <see cref="SessionRecorder"/>, which writes
/// lines and closes its writer, makes a whole page without knowing it is one. Everything else passes
/// through to the writer it wraps, which it closes.
/// </summary>
public sealed class HtmlPageWriter : TextWriter
{
    private readonly TextWriter _inner;
    private bool _closed;

    public HtmlPageWriter(TextWriter inner, string title)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _inner.WriteLine(HtmlText.Head(title));
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);

    public override void Write(string? value) => _inner.Write(value);

    public override void WriteLine(string? value) => _inner.WriteLine(value);

    public override Task WriteLineAsync(string? value) => _inner.WriteLineAsync(value);

    public override Task WriteAsync(string? value) => _inner.WriteAsync(value);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync() => _inner.FlushAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            try
            {
                _inner.WriteLine(HtmlText.Foot);
            }
            finally
            {
                _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            _closed = true;
            try
            {
                await _inner.WriteLineAsync(HtmlText.Foot).ConfigureAwait(false);
            }
            finally
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        // Closed already, so its Dispose has nothing left to write.
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
