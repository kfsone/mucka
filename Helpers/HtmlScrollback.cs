using System.Text;
using Mucka.Core;

namespace Mucka.Helpers;

/// <summary>
/// Converts ANSI-parsed lines to HTML and provides the static terminal page
/// that the GamePage WebView hosts.
///
/// The page keeps at most MAX_LINES lines in the DOM; older lines are trimmed
/// in JavaScript.  Auto-scroll is paused automatically when the user scrolls
/// up and resumed when they return to the bottom.
/// </summary>
public static class HtmlScrollback
{
    // Campbell colour scheme — matches AnsiPalette.cs
    private static readonly string[] HexColors =
    {
        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00",
        "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
        "#767676", "#E74856", "#16C60C", "#F9F1A5",
        "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
    };

    public static readonly string InitialPage = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        * { margin:0; padding:0; box-sizing:border-box; }
        html, body {
          background:#0C0C0C;
          font-family:'Cascadia Mono','Consolas',monospace;
          font-size:15px;
          line-height:1.35;
          color:#CCCCCC;
          overflow-x:auto;
          overflow-y:auto;
        }
        #out { padding:4px 6px; }
        .ln, .lnp { display:block; min-height:1.35em; white-space:pre-wrap; word-break:break-all; }
        </style>
        </head>
        <body>
        <div id="out"></div>
        </body>
        </html>
        """;

    /// <summary>Convert a parsed line to an HTML fragment (no outer element).</summary>
    public static string LineToHtml(StyledLine line)
    {
        if (line.Spans.Count == 0) return string.Empty;
        var sb = new StringBuilder(line.Spans.Count * 40);
        foreach (var span in line.Spans)
        {
            string fg = HexColors[span.Fg < 16 ? span.Fg : 7];
            var style = new StringBuilder($"color:{fg}");
            if (span.Bg != 0) style.Append($";background:{HexColors[span.Bg < 16 ? span.Bg : 0]}");
            if (span.Bold) style.Append(";font-weight:bold");
            sb.Append($"<span style=\"{style}\">{HtmlEncode(span.Text)}</span>");
        }
        return sb.ToString();
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;");
}
