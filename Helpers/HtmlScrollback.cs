using System.Text;
using MudSharp.Models;

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
    // Campbell color scheme — matches AnsiPalette.cs
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
        .echo { color:#767676; font-style:italic; }
        </style>
        </head>
        <body>
        <div id="out"></div>
        <script>
        window._atBottom=true;
        (function(){
          function scrollRoot(){
            return document.scrollingElement||document.documentElement||document.body;
          }
          function chk(){
            var s=scrollRoot();
            var d=s.scrollHeight-s.scrollTop-s.clientHeight;
            var b=d<5;
            if(b===window._atBottom)return;
            window._atBottom=b;
            window.location=b?'mucka://scroll/resume':'mucka://scroll/pause';
          }
          window.addEventListener('scroll',chk,{passive:true});
          document.addEventListener('keydown',function(e){
            if(e.key==='Escape'&&!window._atBottom){
              var s=scrollRoot();
              s.scrollTop=s.scrollHeight;
            }
          });
        })();
        </script>
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
            var fg = span.Style.Foreground == AnsiColor.Default ? 7 : (int)span.Style.Foreground;
            // Classic "bold=bright": SGR 1 + a normal-intensity color (slots 0–7) renders
            // as the bright/intense variant (slots 8–15), matching 1990s terminals and
            // Windows Terminal default behaviour.
            if (span.Style.Bold && fg >= 0 && fg < 8) fg += 8;
            var bg = span.Style.Background == AnsiColor.Default ? -1 : (int)span.Style.Background;
            string fgHex = HexColors[fg >= 0 && fg < 16 ? fg : 7];
            var style = new StringBuilder($"color:{fgHex}");
            if (bg >= 0 && bg < 16) style.Append($";background:{HexColors[bg]}");
            if (span.Style.Bold) style.Append(";font-weight:bold");
            sb.Append($"<span style=\"{style}\">{HtmlEncode(span.Text)}</span>");
        }
        return sb.ToString();
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;");
}
