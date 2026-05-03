// Package ui contains the Gio-based user interface for mucka.
package ui

import (
	"fmt"
	"image"
	"image/color"
	"io"
	"strings"
	"sync"

	"gioui.org/font"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/unit"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// defaultFontSize is the Sp used when no size has been configured.
const defaultFontSize unit.Sp = config.DefaultFontSize

// defaultFontName is the typeface used when no font has been configured.
const defaultFontName = config.DefaultFontName

// Compile-time assertion: *TextPanel must implement core.TextSink.
var _ core.TextSink = (*TextPanel)(nil)

// defaultMaxLines is the default scrollback limit for TextPanel.
const defaultMaxLines = 5000

// TextPanel is an append-only, vertically-scrolling panel that renders
// lines of ANSI-styled Spans.
type TextPanel struct {
	lines          [][]ansi.Span
	maxLines       int // maximum number of lines to keep; 0 = unlimited
	list           layout.List
	pendingMu      sync.Mutex
	pendingLines   [][]ansi.Span
	pendingPartial []ansi.Span // goroutine-safe staging slot for partial line
	partialPending bool        // true when pendingPartial has been updated
	partial        []ansi.Span // current live partial line (main goroutine only)
	fontName       string
	fontSize       unit.Sp

	logMu       sync.Mutex
	logWriter   io.WriteCloser
	logLineFunc func() string // returns line prefix; nil = no prefix
}

// NewTextPanel returns an initialised TextPanel that auto-scrolls to the
// newest content.
func NewTextPanel() *TextPanel {
	return &TextPanel{
		maxLines: defaultMaxLines,
		list: layout.List{
			Axis:        layout.Vertical,
			ScrollToEnd: true,
		},
		fontName: config.DefaultFontName,
		fontSize: defaultFontSize,
	}
}

// SetMaxLines sets the maximum number of lines the panel keeps in memory.
// Older lines are discarded when the limit is exceeded. A value of 0 disables
// the limit (unbounded). The new limit takes effect on the next drainPending call.
func (p *TextPanel) SetMaxLines(n int) { p.maxLines = n }

// SetFont sets the typeface used when rendering text spans.
func (p *TextPanel) SetFont(name string) { p.fontName = name }

// SetFontSize sets the font size used when rendering text and empty lines.
func (p *TextPanel) SetFontSize(sp unit.Sp) { p.fontSize = sp }

// SetLogWriter begins logging appended lines to w. Each line is prefixed with
// the return value of linePrefix (if non-nil). If a log writer is already open
// it is closed first. Goroutine-safe.
func (p *TextPanel) SetLogWriter(w io.WriteCloser, linePrefix func() string) {
	p.logMu.Lock()
	defer p.logMu.Unlock()
	if p.logWriter != nil {
		p.logWriter.Close()
	}
	p.logWriter = w
	p.logLineFunc = linePrefix
}

// StopLog closes the current log writer and stops logging.
// Returns true if logging was active. Goroutine-safe.
func (p *TextPanel) StopLog() bool {
	p.logMu.Lock()
	defer p.logMu.Unlock()
	if p.logWriter == nil {
		return false
	}
	p.logWriter.Close()
	p.logWriter = nil
	p.logLineFunc = nil
	return true
}

// AppendText parses s for ANSI SGR sequences and enqueues the result for
// display on the next frame. Goroutine-safe.
func (p *TextPanel) AppendText(s string) {
	p.AppendSpans(ansi.Parse(s))
}

// AppendSpans enqueues a pre-parsed line of spans for display on the next
// frame, and clears any in-progress partial. Goroutine-safe.
func (p *TextPanel) AppendSpans(spans []ansi.Span) {
	p.logMu.Lock()
	if p.logWriter != nil {
		var sb strings.Builder
		if p.logLineFunc != nil {
			sb.WriteString(p.logLineFunc())
		}
		for _, sp := range spans {
			sb.WriteString(sp.Text)
		}
		fmt.Fprintln(p.logWriter, sb.String())
	}
	p.logMu.Unlock()

	p.pendingMu.Lock()
	p.pendingLines = append(p.pendingLines, spans)
	p.pendingPartial = nil
	p.partialPending = true
	p.pendingMu.Unlock()
}

// UpdatePartial stages a partial (incomplete) line for display on the next
// frame. Goroutine-safe.
func (p *TextPanel) UpdatePartial(spans []ansi.Span) {
	p.pendingMu.Lock()
	p.pendingPartial = spans
	p.partialPending = true
	p.pendingMu.Unlock()
}

// drainPending moves any async-queued lines and partial into the main fields.
// Must be called from the main goroutine (during Layout).
func (p *TextPanel) drainPending() {
	p.pendingMu.Lock()
	pending := p.pendingLines
	p.pendingLines = nil
	hasPartial := p.partialPending
	partial := p.pendingPartial
	p.partialPending = false
	p.pendingMu.Unlock()
	p.lines = append(p.lines, pending...)
	if p.maxLines > 0 && len(p.lines) > p.maxLines {
		excess := len(p.lines) - p.maxLines
		fresh := make([][]ansi.Span, p.maxLines)
		copy(fresh, p.lines[excess:])
		p.lines = fresh
	}
	if hasPartial {
		p.partial = partial // may be nil = clear
	}
}

// Layout renders the text panel into the current constraints.
func (p *TextPanel) Layout(gtx layout.Context, th *material.Theme) layout.Dimensions {
	p.drainPending()
	// Draw background matching the Campbell terminal background (#0C0C0C).
	panelBG := color.NRGBA{R: 0x0C, G: 0x0C, B: 0x0C, A: 255}
	paint.FillShape(gtx.Ops, panelBG, clip.Rect{Max: gtx.Constraints.Max}.Op())

	count := len(p.lines)
	if len(p.partial) > 0 {
		count++
	}
	return p.list.Layout(gtx, count, func(gtx layout.Context, i int) layout.Dimensions {
		var spans []ansi.Span
		if i < len(p.lines) {
			spans = p.lines[i]
		} else {
			spans = p.partial
		}
		return layoutLine(gtx, th, spans, p.fontName, p.fontSize)
	})
}

// layoutLine renders a single line as a horizontal sequence of styled spans.
func layoutLine(gtx layout.Context, th *material.Theme, spans []ansi.Span, fontName string, fontSize unit.Sp) layout.Dimensions {
	if len(spans) == 0 {
		// Empty line: just emit a line-height worth of space.
		h := gtx.Sp(fontSize)
		return layout.Dimensions{Size: image.Point{Y: h}}
	}

	children := make([]layout.FlexChild, len(spans))
	for idx, s := range spans {
		s := s // capture for closure
		children[idx] = layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			return layoutSpan(gtx, th, s, fontName, fontSize)
		})
	}
	return layout.Flex{Axis: layout.Horizontal}.Layout(gtx, children...)
}

// layoutSpan renders one Span: background rectangle + foreground text label.
func layoutSpan(gtx layout.Context, th *material.Theme, s ansi.Span, fontName string, fontSize unit.Sp) layout.Dimensions {
	// Measure the text first, then fill the background, then replay the text.
	macro := op.Record(gtx.Ops)
	lbl := material.Label(th, fontSize, s.Text)
	lbl.Font.Typeface = font.Typeface(fontName)
	lbl.Color = s.FG
	dims := lbl.Layout(gtx)
	call := macro.Stop()

	// Fill background if non-default (skip for pure-black to avoid overdraw).
	if s.BG != ansi.DefaultBG {
		paint.FillShape(gtx.Ops, s.BG, clip.Rect{Max: dims.Size}.Op())
	}

	call.Add(gtx.Ops)
	return dims
}

// image_Point helper removed — using image.Point directly.
