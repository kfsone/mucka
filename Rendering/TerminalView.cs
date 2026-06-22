using Mucka.Terminal;
using MudSharp.Models;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The terminal pane: a SkiaSharp canvas that renders a <see cref="TerminalBuffer"/>.
///
/// Two modes:
/// <list type="bullet">
/// <item><b>Live</b> (default) — the tail of the buffer pinned to the bottom edge; repainted
///   on each flush. No DOM, no cross-process calls, so it does not contend with typing.</item>
/// <item><b>History</b> — entered by scrolling up (wheel / PgUp / touch-drag). A snapshot of the
///   buffer is frozen at entry and the view scrolls within it with a real scrollbar; live output
///   keeps filling the buffer behind the frozen view. Scrolling back to the bottom (or Esc/End)
///   returns to live. While in history the host blocks the input buffer.</item>
/// </list>
/// Lines are naive-hard-wrapped at a fixed column count; selection/copy arrive in a later stage.
/// </summary>
public sealed class TerminalView : SKCanvasView
{
    private readonly TerminalBuffer _buffer = new(cap: 200);
    private TerminalFont? _font;
    private float _builtForSizePx = -1f;
    private int _fontSizeDip = 15;

    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _fillPaint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };

    // Left gutter in device-independent pixels (scaled at paint time), echoing the WebView's padding.
    private const float LeftPadDip = 4f;

    // ── History (scrollback) state ───────────────────────────────────────────
    private bool _historyMode;
    private IReadOnlyList<StyledLine>? _frozen;   // logical snapshot taken at history entry
    private int _scrollOffset;                    // visual rows scrolled up from the bottom (0 = live bottom)
    private int _lastViewportRows = 1;            // updated each paint, used for paging
    private float _touchLastY;
    private float _touchAccum;

    // A click that arrives within this window of the host window being activated is treated as
    // the click that focused the app (→ focus the input box) rather than a deliberate click on
    // the view (→ enter scrollback).
    private const double ActivationClickMs = 300;
    private DateTime _lastActivatedUtc = DateTime.MinValue;

    // A click that arrives within this window of the last keypress is also suppressed from
    // entering scrollback: accidental touchpad taps while typing should not interrupt the session.
    private const double RecentKeypressMs = 300;
    private DateTime _lastKeypressUtc = DateTime.MinValue;

    // ── Selection (mouse drag in history) ────────────────────────────────────
    // Translucent so text shows through; bright enough to be unmistakable on the dark background.
    private static readonly SKColor SelectionColor = new(0x3A, 0x6E, 0xA5, 0x99);
    private bool _selecting;
    private bool _hasSelection;
    private (int Row, int Col) _selAnchor;
    private (int Row, int Col) _selCaret;
    // Geometry cached from the last paint, so pointer events can hit-test rows/columns.
    private List<StyledLine>? _lastRows;
    private int _lastFirst, _lastBottomIndex;
    private float _lastTop, _lastCellW, _lastCellH, _lastLeftPad;
    private float _lastScale = 1f;   // canvas pixels per device-independent pixel (for pointer hit-testing)

    public TerminalView()
    {
        EnableTouchEvents = true;       // touch-drag scrolling (primary on Android)
        PaintSurface += OnPaintSurface;
        Touch += OnTouch;
    }

    /// <summary>Wrap column count (the negotiated EffCols). 0 = wrap to whatever fits the canvas.</summary>
    public int Columns { get; set; }

    /// <summary>True while the user is reviewing frozen scrollback.</summary>
    public bool IsHistoryMode => _historyMode;

    /// <summary>True when there is a non-empty text selection (history mode).</summary>
    public bool HasSelection => _hasSelection;

    /// <summary>Raised when the view enters or leaves history mode.</summary>
    public event EventHandler? HistoryModeChanged;

    /// <summary>Raised when a click should put keyboard focus on the input box (an activation click).</summary>
    public event EventHandler? FocusInputRequested;

    /// <summary>Host calls this when the window is activated, so the next click can be told apart
    /// from a deliberate click on the view.</summary>
    public void NotifyWindowActivated() => _lastActivatedUtc = DateTime.UtcNow;

    /// <summary>Host calls this on every non-modifier keypress so that accidental touchpad taps
    /// while typing do not enter scrollback.</summary>
    public void NotifyKeyPressed() => _lastKeypressUtc = DateTime.UtcNow;

    public void SetFontSize(int dip)
    {
        if (dip <= 0 || dip == _fontSizeDip) return;
        _fontSizeDip = dip;
        _builtForSizePx = -1f;   // force rebuild on next paint
        InvalidateSurface();
    }

    /// <summary>Append a flushed batch of lines. Repaints only when live — the frozen view holds still.</summary>
    public void AppendLines(IReadOnlyList<StyledLine> lines)
    {
        if (lines.Count == 0) return;
        for (int i = 0; i < lines.Count; i++)
        {
            // Strip control-char tofu (\r, \b, …) then expand tabs to 8-col stops before buffering.
            var line = TerminalText.ExpandTabs(TerminalText.Sanitize(lines[i]));
            _buffer.Append(line);
        }
        if (!_historyMode) InvalidateSurface();
    }

    /// <summary>Inject a client annotation line above the live prompt, restoring the prompt below
    /// it (see <see cref="Mucka.Terminal.TerminalBuffer.InjectAbovePartial"/>). Repaints only when live.</summary>
    public void InjectAnnotation(StyledLine line)
    {
        _buffer.InjectAbovePartial(TerminalText.ExpandTabs(TerminalText.Sanitize(line)));
        if (!_historyMode) InvalidateSurface();
    }

    public void Clear()
    {
        _buffer.Clear();
        ExitHistory();          // clearing the screen returns to live
        InvalidateSurface();
    }

    // ── Scroll API (host wires wheel / keys to these) ─────────────────────────

    /// <summary>Scroll by visual rows; positive = toward older output (up). Enters/exits history as needed.</summary>
    public void ScrollByRows(int rowsTowardOlder)
    {
        if (rowsTowardOlder == 0) return;
        if (!_historyMode)
        {
            if (rowsTowardOlder < 0) return;   // already at the live bottom; nothing below
            EnterHistory();
        }
        _scrollOffset += rowsTowardOlder;
        if (_scrollOffset <= 0) ExitHistory();   // scrolled back to the bottom
        InvalidateSurface();
    }

    public void ScrollByPages(int pagesTowardOlder)
        => ScrollByRows(pagesTowardOlder * Math.Max(1, _lastViewportRows - 1));

    public void ScrollToTop()
    {
        if (!_historyMode) EnterHistory();
        _scrollOffset = int.MaxValue / 2;   // clamped to the top in paint
        InvalidateSurface();
    }

    public void ScrollToBottom()
    {
        ExitHistory();
        InvalidateSurface();
    }

    private void EnterHistory()
    {
        if (_historyMode) return;
        _frozen = _buffer.Snapshot();   // freeze logical lines; layout re-wraps responsively in paint
        _historyMode = true;
        _scrollOffset = 0;
        ClearSelection();
        HistoryModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExitHistory()
    {
        if (!_historyMode) return;
        _historyMode = false;
        _frozen = null;
        _scrollOffset = 0;
        ClearSelection();
        HistoryModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSelection()
    {
        _selecting = false;
        _hasSelection = false;
    }

    // Touch-device finger-drag panning (primary on Android). Mouse/pen input is driven by native
    // pointer events on the host via PointerPress/PointerDrag/PointerRelease — SkiaSharp's mouse
    // Touch reporting is unreliable for this on Windows.
    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        if (e.DeviceType == SKTouchDeviceType.Touch)
            HandleTouchPan(e);
    }

    private void HandleTouchPan(SKTouchEventArgs e)
    {
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _touchLastY = e.Location.Y;
                _touchAccum = 0f;
                e.Handled = true;
                break;
            case SKTouchAction.Moved:
                if (e.InContact && _font is { CellHeight: > 0f } f)   // finger down, not a hover
                {
                    _touchAccum += e.Location.Y - _touchLastY;   // dragging down (dy>0) reveals older rows
                    _touchLastY = e.Location.Y;
                    int rows = (int)(_touchAccum / f.CellHeight);
                    if (rows != 0) { _touchAccum -= rows * f.CellHeight; ScrollByRows(rows); }
                    e.Handled = true;
                }
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                e.Handled = true;
                break;
        }
    }

    // ── Pointer API (host drives these from native mouse/pen pointer events) ──
    // Coordinates are in device-independent pixels relative to the view; converted to canvas
    // pixels here using the scale captured at the last paint.

    /// <summary>Mouse/pen press: focus the input on an activation click, otherwise enter scrollback
    /// (if live) and begin a selection.</summary>
    public void PointerPress(float dipX, float dipY)
    {
        if (!_historyMode)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastActivatedUtc).TotalMilliseconds < ActivationClickMs ||
                (now - _lastKeypressUtc).TotalMilliseconds  < RecentKeypressMs)
            {
                FocusInputRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            EnterHistory();
        }
        _selecting = true;
        _selAnchor = _selCaret = HitTest(dipX * _lastScale, dipY * _lastScale);
        _hasSelection = false;   // a bare click clears the previous selection
        InvalidateSurface();
    }

    /// <summary>Mouse/pen drag: extend the selection.</summary>
    public void PointerDrag(float dipX, float dipY)
    {
        if (!_selecting) return;
        _selCaret = HitTest(dipX * _lastScale, dipY * _lastScale);
        _hasSelection = _selCaret != _selAnchor;
        InvalidateSurface();
    }

    /// <summary>Mouse/pen release: finish the selection gesture.</summary>
    public void PointerRelease()
    {
        if (!_selecting) return;
        _selecting = false;
        InvalidateSurface();
    }

    private (int Row, int Col) HitTest(float px, float py)
    {
        if (_lastRows is null || _lastCellH <= 0f || _lastCellW <= 0f) return (_lastFirst, 0);
        int row = Math.Clamp(_lastFirst + (int)((py - _lastTop) / _lastCellH), _lastFirst, _lastBottomIndex);
        int col = Math.Max(0, (int)((px - _lastLeftPad) / _lastCellW));
        return (row, col);
    }

    /// <summary>Copy the current selection (plain text, rows joined by '\n') to the clipboard.
    /// Returns true if non-empty text was copied.</summary>
    public bool CopySelectionToClipboard()
    {
        if (!_hasSelection || _lastRows is null) return false;
        var str = TerminalSelection.Extract(_lastRows, _selAnchor, _selCaret);
        if (str.Length == 0) return false;
        _ = Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.SetTextAsync(str);
        return true;
    }

    private static bool Precedes((int Row, int Col) a, (int Row, int Col) b)
        => a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);

    private static int RowLength(StyledLine row)
    {
        int n = 0;
        for (int i = 0; i < row.Spans.Count; i++) n += row.Spans[i].Text.Length;
        return n;
    }

    private void EnsureFont(float scale)
    {
        float sizePx = _fontSizeDip * scale;
        if (_font is not null && Math.Abs(sizePx - _builtForSizePx) < 0.01f) return;
        _font?.Dispose();
        _font = new TerminalFont(sizePx);
        _builtForSizePx = sizePx;
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
#if INPUT_DIAG
        var _paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var canvas = e.Surface.Canvas;
        canvas.Clear(TerminalTheme.Background);

        int pxW = e.Info.Width, pxH = e.Info.Height;
        if (pxW <= 0 || pxH <= 0 || Width <= 0 || Height <= 0) return;

        float scale = (float)(pxW / Width);
        _lastScale = scale;
        EnsureFont(scale);
        var font = _font!;
        float cellW = font.CellWidth, cellH = font.CellHeight;
        if (cellW <= 0f || cellH <= 0f) return;

        float leftPad = LeftPadDip * scale;
        int colsFit = Math.Max(1, (int)((pxW - leftPad) / cellW));
        int n = Columns > 0 ? Columns : colsFit;

        var rows = BuildVisualRows(n);

        int viewportRows = Math.Max(1, (int)(pxH / cellH));
        _lastViewportRows = viewportRows;
        int maxOffset = Math.Max(0, rows.Count - viewportRows);
        int offset = _historyMode ? Math.Clamp(_scrollOffset, 0, maxOffset) : 0;
        if (_historyMode) _scrollOffset = offset;   // write back the clamped value

        if (rows.Count > 0)
        {
            int bottomIndex = rows.Count - 1 - offset;            // last visible row sits at the bottom edge
            int drawn = Math.Min(viewportRows + 1, bottomIndex + 1);   // +1 lets the top row clip cleanly
            int first = bottomIndex - drawn + 1;
            float top = pxH - drawn * cellH;                      // bottom-pinned window (may be slightly negative)

            // Cache geometry so pointer events can hit-test rows/columns for selection.
            _lastRows = rows; _lastFirst = first; _lastBottomIndex = bottomIndex;
            _lastTop = top; _lastCellW = cellW; _lastCellH = cellH; _lastLeftPad = leftPad;

            // Normalize the selection range (only meaningful while reviewing history).
            bool drawSel = _historyMode && (_hasSelection || _selecting);
            (int Row, int Col) selA = default, selB = default;
            if (drawSel)
                (selA, selB) = Precedes(_selAnchor, _selCaret) ? (_selAnchor, _selCaret) : (_selCaret, _selAnchor);

            for (int r = first; r <= bottomIndex; r++)
            {
                float rowTop = top + (r - first) * cellH;
                var row = rows[r];

                // Glyphs first.
                float baseline = rowTop + font.Baseline;
                float x = leftPad;
                for (int s = 0; s < row.Spans.Count; s++)
                {
                    var run = row.Spans[s];
                    float runW = run.Text.Length * cellW;
                    if (TerminalTheme.SpanBackground(run.Style) is { } bgColor)
                    {
                        _fillPaint.Color = bgColor;
                        canvas.DrawRect(x, rowTop, runW, cellH, _fillPaint);
                    }
                    _textPaint.Color = TerminalTheme.Foreground(run.Style);
                    // Windows Terminal renders intense text bold AND bright; mirror the weight
                    // with a stroke-and-fill fake bold (advances unchanged → grid stays aligned).
                    if (run.Style.Bold)
                    {
                        _textPaint.Style = SKPaintStyle.StrokeAndFill;
                        _textPaint.StrokeWidth = font.BoldStrokeWidth;
                    }
                    else
                    {
                        _textPaint.Style = SKPaintStyle.Fill;
                    }
                    canvas.DrawText(run.Text, x, baseline, font.Font, _textPaint);
                    x += runW;
                }

                // Selection highlight as a translucent overlay ON TOP of the glyphs, so it is
                // visible regardless of any span backgrounds underneath.
                if (drawSel && r >= selA.Row && r <= selB.Row)
                {
                    int rowLen = RowLength(row);
                    int a = Math.Clamp(r == selA.Row ? selA.Col : 0,      0, rowLen);
                    int b = Math.Clamp(r == selB.Row ? selB.Col : rowLen, 0, rowLen);
                    if (b > a)
                    {
                        _fillPaint.Color = SelectionColor;
                        canvas.DrawRect(leftPad + a * cellW, rowTop, (b - a) * cellW, cellH, _fillPaint);
                    }
                }
            }
        }
        else
        {
            _lastRows = null;
        }

        if (_historyMode)
            DrawHistoryScrollbar(canvas, pxW, pxH, scale, rows.Count, viewportRows, offset, maxOffset);
        else
            DrawLiveScrollbar(canvas, pxW, pxH, scale, rows.Count, viewportRows);

#if INPUT_DIAG
        var _paintMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _paintStart)
                       * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Mucka.Core.InputDiag.Log($"PAINT {_paintMs:F1}ms {(_historyMode ? "hist" : "live")} visrows={rows.Count}");
#endif
    }

    // Wrap the active source (frozen snapshot in history, else live buffer) into visual rows.
    private List<StyledLine> BuildVisualRows(int n)
    {
        if (_historyMode && _frozen is not null)
            return LineWrapper.WrapAll(_frozen, n);

        var rows = LineWrapper.WrapAll(_buffer.Committed, n);
        if (_buffer.Partial is { } partial)
            LineWrapper.Wrap(partial, n, rows);
        return rows;
    }

    // Live: faint thumb hugging the bottom — purely indicative that there is more above.
    private void DrawLiveScrollbar(SKCanvas canvas, int pxW, int pxH, float scale, int contentRows, int viewportRows)
    {
        if (contentRows <= viewportRows || viewportRows <= 0) return;
        float barW = 3f * scale;
        float x = pxW - barW;
        _fillPaint.Color = new SKColor(0x33, 0x33, 0x33, 0x80);
        canvas.DrawRect(x, 0, barW, pxH, _fillPaint);
        float thumbH = Math.Max(8f * scale, pxH * viewportRows / (float)contentRows);
        _fillPaint.Color = new SKColor(0x66, 0x66, 0x66, 0xC0);
        canvas.DrawRect(x, pxH - thumbH, barW, thumbH, _fillPaint);
    }

    // History: a real scrollbar whose thumb tracks the scroll position (bottom = live edge).
    private void DrawHistoryScrollbar(
        SKCanvas canvas, int pxW, int pxH, float scale, int contentRows, int viewportRows, int offset, int maxOffset)
    {
        if (contentRows <= viewportRows) return;
        float barW = 4f * scale;
        float x = pxW - barW;
        _fillPaint.Color = new SKColor(0x33, 0x33, 0x33, 0xB0);
        canvas.DrawRect(x, 0, barW, pxH, _fillPaint);
        float thumbH = Math.Max(12f * scale, pxH * viewportRows / (float)contentRows);
        float frac = maxOffset > 0 ? (float)(maxOffset - offset) / maxOffset : 1f;   // 1 = bottom
        float thumbTop = frac * (pxH - thumbH);
        _fillPaint.Color = new SKColor(0x99, 0x99, 0x99, 0xE0);
        canvas.DrawRect(x, thumbTop, barW, thumbH, _fillPaint);
    }
}
