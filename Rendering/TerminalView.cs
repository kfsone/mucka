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
/// <item><b>Live</b> (default) - the tail of the buffer pinned to the bottom edge; repainted
///   on each flush. No DOM, no cross-process calls, so it does not contend with typing.</item>
/// <item><b>History</b> - entered by scrolling up (wheel / PgUp / touch-drag). A snapshot of the
///   buffer is frozen at entry and the view scrolls within it with a real scrollbar; live output
///   keeps filling the buffer behind the frozen view. Scrolling back to the bottom (or Esc/End)
///   returns to live. While in history the host blocks the input buffer.</item>
/// </list>
/// A third, <b>document</b> mode (<see cref="ShowDocument"/>) is history that never returns to live:
/// a fixed list of lines to read - the History page's Session viewer - with a search match drawn
/// over it (<see cref="ShowMatch"/>).
/// A clear-screen from the server empties the live view but not history: the snapshot holds the
/// lines above it, with a drawn rule where the clear was (see <see cref="TerminalBuffer.ClearRule"/>).
/// <see cref="Clear"/> is the client's own wipe and takes history with it.
/// Lines are word-wrapped by <see cref="LineWrapper"/> at the column count, on every paint. Selection and copy are handled here.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The paints and fonts live as long as the view does, and nothing disposes a MAUI view; a Dispose here would never be called.")]
public sealed class TerminalView : SKCanvasView
{
    /// <summary>
    /// Logical lines of scrollback the pane holds. Each live repaint re-wraps the whole committed
    /// buffer (see BuildVisualRows), so this is the per-paint wrap cost - but wrapping this many
    /// short lines is sub-millisecond and only one viewport's worth is ever drawn. A repaint from
    /// the view model's pane history can only restore what that history kept, so its main ring
    /// derives from this.
    /// </summary>
    public const int BufferCap = 500;
    private readonly TerminalBuffer _buffer = new(cap: BufferCap);
    private TerminalFont? _font;
    private float _builtForSizePx = -1f;
    private int _fontSizeDip = 15;

    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _fillPaint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };

    // Left gutter in device-independent pixels (scaled at paint time), echoing the WebView's padding.
    private const float LeftPadDip = 4f;

    // -- History (scrollback) state -------------------------------------------
    private bool _historyMode;
    private IReadOnlyList<StyledLine>? _frozen;   // logical snapshot taken at history entry
    private int _scrollOffset;                    // visual rows scrolled up from the bottom (0 = live bottom)
    private int _lastViewportRows = 1;            // updated each paint, used for paging
    private float _touchLastY;
    private float _touchAccum;

    // -- Document mode, and the wrap of a frozen list ---------------------------
    private bool _document;
    private TextMatch? _match;
    private bool _revealMatch;
    // The frozen list wrapped at a column count, kept until either changes: a document can be many
    // thousands of lines, and history repaints on every scroll step and selection move.
    private IReadOnlyList<StyledLine>? _wrapSource;
    private int _wrapColumns;
    private List<StyledLine> _wrapRows = [];
    private List<bool> _wrapContinues = [];
    // The first visual row of each logical line of _wrapSource.
    private List<int> _wrapLineRows = [];
    // A search match: drawn over the glyphs, as the selection is, in a colour of its own.
    private static readonly SKColor MatchColor = new(0xD2, 0x99, 0x22, 0x88);

    // A click that arrives within this window of the host window being activated is treated as
    // the click that focused the app (-> focus the input box) rather than a deliberate click on
    // the view (-> enter scrollback).
    private const double ActivationClickMs = 300;
    private DateTime _lastActivatedUtc = DateTime.MinValue;

    // A click that arrives within this window of the last keypress is also suppressed from
    // entering scrollback: accidental touchpad taps while typing should not interrupt the session.
    private const double RecentKeypressMs = 300;
    private DateTime _lastKeypressUtc = DateTime.MinValue;

    // -- Selection (mouse drag in history) -------------------------------------
    // Translucent so text shows through; bright enough to be unmistakable on the dark background.
    private static readonly SKColor SelectionColor = new(0x3A, 0x6E, 0xA5, 0x99);
    // The clear-screen rule in scrollback: the palette's dark grey, the colour of client notes.
    private static readonly SKColor ClearRuleColor = TerminalTheme.Palette[8];
    // A plain click must NOT enter scrollback - only a deliberate drag does (along with the
    // wheel and PgUp/PgDn keys). On a live press we arm a pending drag and stay live; scrollback
    // begins the moment the pointer moves past this threshold (see PointerDrag).
    private const float DragThresholdDip = 4f;
    private bool _pendingPress;          // live press awaiting a drag before it counts as scrollback
    private float _pressX, _pressY;      // where the live press landed (dip), to seed the selection anchor
    private bool _selecting;
    private bool _hasSelection;

    // -- Scrollbar (mouse, in history) ------------------------------------------
    // The strip (CombatRailResize.TerminalScrollbarDp) is narrow, so a press within this width of the right edge is on it.
    private const float ScrollbarHitDp = 10f;
    // The scrollbar as last drawn, in canvas pixels; _barHitLeftPx is infinity while none is drawn.
    private float _barHitLeftPx = float.PositiveInfinity;
    private float _thumbTopPx, _thumbHeightPx, _trackHeightPx;
    private int _barMaxOffset;
    private bool _barDragging;
    private float _barGrabPx;            // how far down the thumb the press took hold
    private (int Row, int Col) _selAnchor;
    private (int Row, int Col) _selCaret;
    // Geometry cached from the last paint, so pointer events can hit-test rows/columns.
    private List<StyledLine>? _lastRows;
    // Which of _lastRows continue the row above (a soft wrap) rather than starting a logical line.
    // Built with _lastRows and assigned with it; the copy path is the only reader.
    private List<bool>? _lastRowContinues;
    private int _lastFirst, _lastBottomIndex;
    private float _lastTop, _lastCellW, _lastCellH, _lastLeftPad;
    private float _lastScale = 1f;   // canvas pixels per device-independent pixel (for pointer hit-testing)

    public TerminalView()
    {
        EnableTouchEvents = true;       // touch-drag scrolling (primary on Android)
        PaintSurface += OnPaintSurface;
        Touch += OnTouch;
    }

    private int _columns;

    /// <summary>Wrap column count (the pane's PaneColumns), capped at what the canvas fits. 0 = wrap
    /// to whatever fits the canvas. A change repaints, because everything on screen is wrapped
    /// again. UI thread only.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            if (_columns == value) return;
            _columns = value;
            InvalidateSurface();
        }
    }

    /// <summary>True while the user is reviewing frozen scrollback.</summary>
    public bool IsHistoryMode => _historyMode;

    /// <summary>True when there is a non-empty text selection (history mode).</summary>
    public bool HasSelection => _hasSelection;

    /// <summary>Raised when the view enters or leaves history mode.</summary>
    public event EventHandler? HistoryModeChanged;

    /// <summary>Raised when a click should put keyboard focus on the input box (an activation click).</summary>
    public event EventHandler? FocusInputRequested;

    /// <summary>
    /// Raised when the user clicks a span carrying <see cref="StyledSpan.ClickInsertText"/>.
    /// Hosts may insert the payload into their input control when appropriate.
    /// </summary>
    public event Action<string>? SpanInsertTextRequested;

    /// <summary>Host calls this when the window is activated, so the next click can be told apart
    /// from a deliberate click on the view.</summary>
    public void NotifyWindowActivated() => _lastActivatedUtc = DateTime.UtcNow;

    /// <summary>Host calls this on every non-modifier keypress so that accidental touchpad taps
    /// while typing do not enter scrollback.</summary>
    public void NotifyKeyPressed() => _lastKeypressUtc = DateTime.UtcNow;

    /// <summary>
    /// The chat colours and faces every chat span is drawn in, looked up at paint time: changing
    /// them restyles everything the pane holds, live or frozen scrollback. UI thread only.
    /// </summary>
    public ChatColorizer.Palette ChatPalette
    {
        get => _chatPalette;
        set
        {
            if (value == _chatPalette) return;
            _chatPalette = value;
            InvalidateSurface();
        }
    }
    private ChatColorizer.Palette _chatPalette = ChatColorizer.DefaultPalette;

    /// <summary>The colour the pane is cleared to: the theme's, unless a view of the same text is set to
    /// look different from the game (the Logs viewer).</summary>
    public SkiaSharp.SKColor ClearColor
    {
        get => _clearColor;
        set
        {
            if (value == _clearColor) return;
            _clearColor = value;
            InvalidateSurface();
        }
    }
    private SkiaSharp.SKColor _clearColor = TerminalTheme.Background;

    /// <summary>The font size the pane draws at, in dip.</summary>
    public int FontSizeDip => _fontSizeDip;

    public void SetFontSize(int dip)
    {
        if (dip <= 0 || dip == _fontSizeDip) return;
        _fontSizeDip = dip;
        _builtForSizePx = -1f;   // force rebuild on next paint
        InvalidateSurface();
    }

    /// <summary>Append a flushed batch of lines. Repaints only when live - the frozen view holds still.</summary>
    public void AppendLines(IReadOnlyList<StyledLine> lines)
    {
        if (lines.Count == 0) return;
        for (int i = 0; i < lines.Count; i++)
        {
            // Strip control-char tofu (\r, \b, ...) then expand tabs to 8-col stops before buffering.
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

    /// <summary>Shows <paramref name="lines"/> to be read, from the top, in place of the live screen
    /// and for good: scrolling and selection work as in scrollback, and nothing returns the view to
    /// live. The lines are lines of screen already (<see cref="TerminalBuffer.ScreenOf"/>). UI thread
    /// only.</summary>
    public void ShowDocument(IReadOnlyList<StyledLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _document = true;
        _historyMode = true;
        _frozen = lines;
        ForgetWrap();
        _scrollOffset = int.MaxValue / 2;   // clamped to the top in paint
        _match = null;
        _revealMatch = false;
        _pendingPress = false;
        ClearSelection();
        InvalidateSurface();
    }

    /// <summary>Draws <paramref name="match"/> over the document and scrolls it to the middle of the
    /// view; null takes the drawn match away and leaves the view where it is.</summary>
    public void ShowMatch(TextMatch? match)
    {
        _match = match;
        _revealMatch = match is not null;
        InvalidateSurface();
    }

    /// <summary>The first and last logical lines of the document the view showed at its last paint,
    /// or null before one.</summary>
    public (int First, int Last)? ShownLines
    {
        get
        {
            if (!_document || _lastRows is null || !ReferenceEquals(_lastRows, _wrapRows)
                || !ReferenceEquals(_wrapSource, _frozen) || _wrapLineRows.Count == 0)
                return null;
            return (LineOfRow(_lastFirst), LineOfRow(_lastBottomIndex));
        }
    }

    // Lets the kept wrap go with the list it was made from, rather than at the next paint.
    private void ForgetWrap()
    {
        _wrapSource = null;
        _wrapRows = [];
        _wrapContinues = [];
        _wrapLineRows = [];
    }

    private int LineOfRow(int row)
    {
        var at = _wrapLineRows.BinarySearch(row);
        return at >= 0 ? at : Math.Max(0, ~at - 1);
    }

    public void Clear()
    {
        _buffer.Clear();
        ExitHistory();          // clearing the screen returns to live
        InvalidateSurface();
    }

    // -- Scroll API (host wires wheel / keys to these) -------------------------

    /// <summary>One row's height in device-independent units, or 0 before the first paint. The
    /// font is sized in surface pixels, so this divides out the surface's scale.</summary>
    public double RowHeightDp
        => _font is { CellHeight: > 0f } f && Width > 0 && CanvasSize.Width > 0
            ? f.CellHeight / (CanvasSize.Width / Width)
            : 0.0;

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
        if (_scrollOffset <= 0)
        {
            // Scrolled back to the bottom: live again, or a document's last line.
            if (_document) _scrollOffset = 0;
            else ExitHistory();
        }
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
        if (_document) _scrollOffset = 0;
        else ExitHistory();
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
        if (!_historyMode || _document) return;
        _historyMode = false;
        _frozen = null;
        ForgetWrap();
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
    // pointer events on the host via PointerPress/PointerDrag/PointerRelease - SkiaSharp's mouse
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

    // -- Pointer API (host drives these from native mouse/pen pointer events) --
    // Coordinates are in device-independent pixels relative to the view; converted to canvas
    // pixels here using the scale captured at the last paint.

    /// <summary>Mouse/pen press. In live mode a plain click never enters scrollback: an activation
    /// click focuses the input, any other press just arms a pending drag and stays live (scrollback
    /// begins only if the pointer then moves - see <see cref="PointerDrag"/>). In history mode a
    /// press starts a fresh selection anchor.</summary>
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
            // Stay live; remember where the press landed so a subsequent drag can seed the
            // selection anchor. A press with no drag leaves us live (the click is free for
            // span-insert / focus, never scrollback).
            _pendingPress = true;
            _pressX = dipX;
            _pressY = dipY;
            return;
        }
        if (dipX * _lastScale >= _barHitLeftPx)
        {
            PressScrollbar(dipY * _lastScale);
            return;
        }
        _selecting = true;
        _selAnchor = _selCaret = HitTest(dipX * _lastScale, dipY * _lastScale);
        _hasSelection = false;   // a bare click clears the previous selection
        InvalidateSurface();
    }

    /// <summary>Mouse/pen drag: from a live press, crossing the drag threshold enters scrollback and
    /// begins a selection from the press point; thereafter it extends the selection.</summary>
    public void PointerDrag(float dipX, float dipY)
    {
        if (_barDragging)
        {
            DragThumb(dipY * _lastScale);
            return;
        }
        if (_pendingPress)
        {
            if (Math.Abs(dipX - _pressX) < DragThresholdDip && Math.Abs(dipY - _pressY) < DragThresholdDip)
                return;   // not yet moved enough to count as a deliberate drag
            _pendingPress = false;
            EnterHistory();                 // clears any prior selection
            _selecting = true;
            _selAnchor = HitTest(_pressX * _lastScale, _pressY * _lastScale);
            _selCaret  = HitTest(dipX * _lastScale, dipY * _lastScale);
            _hasSelection = _selCaret != _selAnchor;
            InvalidateSurface();
            return;
        }
        if (!_selecting) return;
        _selCaret = HitTest(dipX * _lastScale, dipY * _lastScale);
        _hasSelection = _selCaret != _selAnchor;
        InvalidateSurface();
    }

    /// <summary>Mouse/pen release: finish the selection gesture. A live press that never dragged
    /// simply disarms - the view stayed live throughout.</summary>
    public void PointerRelease()
    {
        _pendingPress = false;
        if (_barDragging)
        {
            _barDragging = false;
            // A thumb let go at the bottom of scrollback is at the live edge.
            if (_scrollOffset <= 0 && !_document) ExitHistory();
            InvalidateSurface();
            return;
        }
        if (!_selecting) return;
        _selecting = false;
        InvalidateSurface();
    }

    // A press on the scrollbar in history: on the thumb it takes hold of it, on the track it pages
    // toward the press. py is in canvas pixels.
    private void PressScrollbar(float py)
    {
        if (py >= _thumbTopPx && py < _thumbTopPx + _thumbHeightPx)
        {
            _barDragging = true;
            _barGrabPx = py - _thumbTopPx;
        }
        else
        {
            ScrollByPages(py < _thumbTopPx ? 1 : -1);
        }
    }

    // The thumb follows the pointer from where it was taken hold of.
    private void DragThumb(float py)
    {
        _scrollOffset = ScrollbarThumb.Offset(_trackHeightPx, _thumbHeightPx, py - _barGrabPx, _barMaxOffset);
        InvalidateSurface();
    }

    /// <summary>Double click: selects the word under the point (see <see cref="TerminalSelection.WordBounds"/>).
    /// A live view enters scrollback first, as a drag does, unless the click is an activation or
    /// follows a keypress. Returns true when a selection was made.</summary>
    public bool SelectWordAt(float dipX, float dipY) => SelectAt(dipX, dipY, lineWise: false);

    /// <summary>Triple click: selects the whole logical line under the point, across its soft wraps.</summary>
    public bool SelectLineAt(float dipX, float dipY) => SelectAt(dipX, dipY, lineWise: true);

    private bool SelectAt(float dipX, float dipY, bool lineWise)
    {
        if (_lastRows is null) return false;
        if (!_historyMode)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastActivatedUtc).TotalMilliseconds < ActivationClickMs ||
                (now - _lastKeypressUtc).TotalMilliseconds < RecentKeypressMs)
                return false;
            _pendingPress = false;
            EnterHistory();
        }
        var rows = _lastRows;
        var (row, col) = HitTest(dipX * _lastScale, dipY * _lastScale);
        if (row < 0 || row >= rows.Count || TerminalBuffer.IsClearRule(rows[row])) return false;

        if (lineWise)
        {
            var (first, last) = TerminalSelection.LineRows(_lastRowContinues, rows.Count, row);
            _selAnchor = (first, 0);
            _selCaret = (last, RowLength(rows[last]));
        }
        else
        {
            if (TerminalSelection.WordBounds(rows[row].PlainText, col) is not { } w) return false;
            _selAnchor = (row, w.Start);
            _selCaret = (row, w.End);
        }
        _selecting = false;
        _hasSelection = _selCaret != _selAnchor;
        InvalidateSurface();
        return _hasSelection;
    }

    /// <summary>
    /// In live mode, hit-tests a clickable span and raises <see cref="SpanInsertTextRequested"/>
    /// with its insertion text. Returns true when a clickable span consumed the click.
    /// </summary>
    public bool TryActivateSpanInsert(float dipX, float dipY)
    {
        if (_historyMode || _lastRows is null || _lastCellH <= 0f || _lastCellW <= 0f)
            return false;

        var (row, col) = HitTest(dipX * _lastScale, dipY * _lastScale);
        if (row < 0 || row >= _lastRows.Count)
            return false;

        var spans = _lastRows[row].Spans;
        int at = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            int next = at + span.Text.Length;
            if (col >= at && col < next)
            {
                if (span.ClickInsertText is { Length: > 0 } insert)
                {
                    SpanInsertTextRequested?.Invoke(insert);
                    return true;
                }
                return false;
            }
            at = next;
        }
        return false;
    }

    private (int Row, int Col) HitTest(float px, float py)
    {
        if (_lastRows is null || _lastCellH <= 0f || _lastCellW <= 0f) return (_lastFirst, 0);
        int row = Math.Clamp(_lastFirst + (int)((py - _lastTop) / _lastCellH), _lastFirst, _lastBottomIndex);
        int col = Math.Max(0, (int)((px - _lastLeftPad) / _lastCellW));
        return (row, col);
    }

    /// <summary>Copy the current selection to the clipboard as plain text. Rows are joined with a
    /// newline only across a hard break - a soft wrap does not survive the copy, so a long line the
    /// client printed pastes as one string. See <see cref="TerminalSelection"/>.
    /// Returns true if non-empty text was copied.</summary>
    public bool CopySelectionToClipboard()
    {
        if (!_hasSelection || _lastRows is null) return false;
        var str = TerminalSelection.Extract(_lastRows, _selAnchor, _selCaret, _lastRowContinues);
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

    // The kept wrap's row holding the given column of the match's line (a line's rows concatenate
    // back to its text, see LineWrapper); null when the wrap is not of the document, or the line is
    // past its end.
    private int? MatchRow(TextMatch match, int column)
    {
        if (!ReferenceEquals(_wrapSource, _frozen) || match.Line < 0 || match.Line >= _wrapLineRows.Count)
            return null;
        int first = _wrapLineRows[match.Line], last = first + RowsOfLine(match.Line) - 1, at = 0;
        for (int r = first; r < last; r++)
        {
            at += RowLength(_wrapRows[r]);
            if (column < at) return r;
        }
        return last;
    }

    private int RowsOfLine(int line)
        => (line + 1 < _wrapLineRows.Count ? _wrapLineRows[line + 1] : _wrapRows.Count) - _wrapLineRows[line];

    // The column of the line's text that the row starts at.
    private int ColumnOfRow(int line, int row)
    {
        int at = 0;
        for (int r = _wrapLineRows[line]; r < row; r++) at += RowLength(_wrapRows[r]);
        return at;
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
        canvas.Clear(_clearColor);

        int pxW = e.Info.Width, pxH = e.Info.Height;
        if (pxW <= 0 || pxH <= 0 || Width <= 0 || Height <= 0) return;

        float scale = (float)(pxW / Width);
        _lastScale = scale;
        EnsureFont(scale);
        var font = _font!;
        float cellW = font.CellWidth, cellH = font.CellHeight;
        if (cellW <= 0f || cellH <= 0f) return;

        float leftPad = LeftPadDip * scale;
        float scrollbarPx = (float)Mucka.Combat.CombatRailResize.TerminalScrollbarDp * scale;
        int colsFit = Math.Max(1, (int)((pxW - leftPad - scrollbarPx) / cellW));
        int n = Columns > 0 ? Math.Min(Columns, colsFit) : colsFit;

        var rows = BuildVisualRows(n, out var continues, out int liveFirstRow);

        int viewportRows = Math.Max(1, (int)(pxH / cellH));
        _lastViewportRows = viewportRows;
        int maxOffset = Math.Max(0, rows.Count - viewportRows);
        if (_revealMatch && _document && _match is { } reveal && MatchRow(reveal, reveal.Start) is { } matchRow)
        {
            // The match's first row half way down the view, as far as the ends allow.
            _scrollOffset = rows.Count - 1 - matchRow - viewportRows / 2;
            _revealMatch = false;
        }
        int offset = _historyMode ? Math.Clamp(_scrollOffset, 0, maxOffset) : 0;
        if (_historyMode) _scrollOffset = offset;   // write back the clamped value

        if (rows.Count > 0)
        {
            int bottomIndex = rows.Count - 1 - offset;            // last visible row sits at the bottom edge
            int drawn = Math.Min(viewportRows + 1, bottomIndex + 1);   // +1 lets the top row clip cleanly
            // Live: a server clear-screen empties the screen - nothing at or above its rule is drawn.
            if (!_historyMode) drawn = Math.Min(drawn, bottomIndex + 1 - liveFirstRow);
            int first = bottomIndex - drawn + 1;
            float top = pxH - drawn * cellH;                      // bottom-pinned window (may be slightly negative)

            // Cache geometry so pointer events can hit-test rows/columns for selection.
            // first is past bottomIndex on a live screen emptied by a clear; HitTest clamps into
            // [_lastFirst, _lastBottomIndex], so keep that range non-empty.
            _lastRows = rows; _lastRowContinues = continues; _lastFirst = Math.Min(first, bottomIndex); _lastBottomIndex = bottomIndex;
            _lastTop = top; _lastCellW = cellW; _lastCellH = cellH; _lastLeftPad = leftPad;

            // Normalize the selection range (only meaningful while reviewing history).
            bool drawSel = _historyMode && (_hasSelection || _selecting);
            (int Row, int Col) selA = default, selB = default;
            if (drawSel)
                (selA, selB) = Precedes(_selAnchor, _selCaret) ? (_selAnchor, _selCaret) : (_selCaret, _selAnchor);

            // Every background before any glyph: an italic glyph leans past the right edge of its
            // cell, and a later span's background rect must not paint over it.
            for (int r = first; r <= bottomIndex; r++)
            {
                var row = rows[r];
                if (TerminalBuffer.IsClearRule(row)) continue;
                float rowTop = top + (r - first) * cellH;
                float x = leftPad;
                for (int s = 0; s < row.Spans.Count; s++)
                {
                    var run = row.Spans[s];
                    float runW = run.Text.Length * cellW;
                    if (TerminalTheme.SpanBackground(run.Style) is { } bgColor)
                    {
                        // A black background is the pane's own colour, which this view may have lifted.
                        _fillPaint.Color = bgColor == TerminalTheme.Background ? _clearColor : bgColor;
                        canvas.DrawRect(x, rowTop, runW, cellH, _fillPaint);
                    }
                    x += runW;
                }
            }

            for (int r = first; r <= bottomIndex; r++)
            {
                float rowTop = top + (r - first) * cellH;
                var row = rows[r];

                // Where the server cleared the screen: only scrollback ever shows these rows. A
                // drawn rule across the text columns - no glyph, and no selection highlight.
                if (TerminalBuffer.IsClearRule(row))
                {
                    _fillPaint.Color = ClearRuleColor;
                    float ruleH = Math.Max(1f, scale);
                    float ruleW = Math.Min(n * cellW, pxW - leftPad);
                    canvas.DrawRect(leftPad, rowTop + (cellH - ruleH) / 2f, ruleW, ruleH, _fillPaint);
                    continue;
                }

                // Glyphs, each run starting on its column: italic and upright runs share the cell grid.
                float baseline = rowTop + font.Baseline;
                float x = leftPad;
                for (int s = 0; s < row.Spans.Count; s++)
                {
                    var run = row.Spans[s];
                    float runW = run.Text.Length * cellW;
                    _textPaint.Color = TerminalTheme.Foreground(run.Style, _chatPalette);
                    var face = _chatPalette.FaceOf(run.Style);
                    var runFont = (face & ChatFace.Italic) != 0 ? font.ItalicFont : font.Font;
                    // Windows Terminal renders intense text bold AND bright; mirror the weight
                    // with a stroke-and-fill fake bold (advances unchanged -> grid stays aligned).
                    if ((face & ChatFace.Bold) != 0)
                    {
                        _textPaint.Style = SKPaintStyle.StrokeAndFill;
                        _textPaint.StrokeWidth = font.BoldStrokeWidth;
                    }
                    else
                    {
                        _textPaint.Style = SKPaintStyle.Fill;
                    }
                    canvas.DrawText(run.Text, x, baseline, runFont, _textPaint);
                    if (run.Style.Underline)
                    {
                        var y = baseline + Math.Max(1f, cellH * 0.06f);
                        var oldStyle = _textPaint.Style;
                        var oldStroke = _textPaint.StrokeWidth;
                        _textPaint.Style = SKPaintStyle.Fill;
                        canvas.DrawRect(x, y, runW, Math.Max(1f, cellH * 0.05f), _textPaint);
                        _textPaint.Style = oldStyle;
                        _textPaint.StrokeWidth = oldStroke;
                    }
                    x += runW;
                }

                // The search match, over the glyphs as the selection is: the part of it on this row.
                if (_document && _match is { } match && MatchRow(match, match.Start) is { } matchFirst
                    && r >= matchFirst && r < _wrapLineRows[match.Line] + RowsOfLine(match.Line))
                {
                    int rowStart = ColumnOfRow(match.Line, r);
                    int a = Math.Max(0, match.Start - rowStart);
                    int b = Math.Min(RowLength(row), match.Start + match.Length - rowStart);
                    if (b > a)
                    {
                        _fillPaint.Color = MatchColor;
                        canvas.DrawRect(leftPad + a * cellW, rowTop, (b - a) * cellW, cellH, _fillPaint);
                    }
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
            _lastRows = null; _lastRowContinues = null;
        }

        _barHitLeftPx = float.PositiveInfinity;
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

    // Wrap the active source (frozen snapshot in history, else live buffer) into visual rows, and
    // record which of them are soft-wrap continuations - the copy path strips those breaks.
    //
    // Live wraps the whole buffer too, so a row index means the same row in both modes - a drag
    // from live seeds its selection from live geometry and then paints over the frozen rows.
    // liveFirstRow is where the live screen starts (the row after the latest clear-screen rule);
    // the live window is never drawn above it.
    //
    // A frozen list is wrapped once per column count and the rows kept (_wrapRows): nothing in it
    // changes until it is replaced. The rows and flags returned are then the kept ones, read only.
    private List<StyledLine> BuildVisualRows(int n, out List<bool> continues, out int liveFirstRow)
    {
        liveFirstRow = 0;
        if (_historyMode && _frozen is not null)
        {
            if (!ReferenceEquals(_wrapSource, _frozen) || _wrapColumns != n)
            {
                var wrapContinues = new List<bool>();
                _wrapRows = LineWrapper.WrapAll(_frozen, n, wrapContinues);
                _wrapContinues = wrapContinues;
                _wrapLineRows = new List<int>(_frozen.Count);
                for (int r = 0; r < wrapContinues.Count; r++)
                    if (!wrapContinues[r]) _wrapLineRows.Add(r);
                _wrapSource = _frozen;
                _wrapColumns = n;
            }
            continues = _wrapContinues;
            return _wrapRows;
        }

        continues = new List<bool>();
        var rows = new List<StyledLine>();
        var committed = _buffer.Committed;
        for (int i = 0; i < committed.Count; i++)
        {
            if (i == _buffer.LiveStart) liveFirstRow = rows.Count;
            LineWrapper.Wrap(committed[i], n, rows, continues);
        }
        if (_buffer.LiveStart >= committed.Count) liveFirstRow = rows.Count;
        if (_buffer.Partial is { } partial)
            LineWrapper.Wrap(partial, n, rows, continues);
        return rows;
    }

    // Live: faint thumb hugging the bottom - purely indicative that there is more above.
    private void DrawLiveScrollbar(SKCanvas canvas, int pxW, int pxH, float scale, int contentRows, int viewportRows)
    {
        if (contentRows <= viewportRows || viewportRows <= 0) return;
        float barW = (float)Mucka.Combat.CombatRailResize.TerminalScrollbarDp * scale;
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
        float barW = (float)Mucka.Combat.CombatRailResize.TerminalScrollbarDp * scale;
        float x = pxW - barW;
        _fillPaint.Color = new SKColor(0x33, 0x33, 0x33, 0xB0);
        canvas.DrawRect(x, 0, barW, pxH, _fillPaint);
        float thumbH = ScrollbarThumb.Height(pxH, viewportRows, contentRows, minimum: 12f * scale);
        float thumbTop = ScrollbarThumb.Top(pxH, thumbH, offset, maxOffset);
        _fillPaint.Color = new SKColor(0x99, 0x99, 0x99, 0xE0);
        canvas.DrawRect(x, thumbTop, barW, thumbH, _fillPaint);
        _barHitLeftPx = pxW - Math.Max(barW, ScrollbarHitDp * scale);
        _thumbTopPx = thumbTop;
        _thumbHeightPx = thumbH;
        _trackHeightPx = pxH;
        _barMaxOffset = maxOffset;
    }
}
