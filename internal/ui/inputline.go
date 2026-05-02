package ui

import (
	"fmt"
	"image/color"
	"io"
	"strings"
	"sync"

	"gioui.org/font"
	"gioui.org/io/clipboard"
	"gioui.org/io/event"
	"gioui.org/io/key"
	"gioui.org/io/transfer"
	"gioui.org/layout"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/unit"
	"gioui.org/widget"
	"gioui.org/widget/material"
)

// Op kind constants for EnqueueOp / drainOps.
const (
	OpText   = "text"
	OpBS     = "bs"
	OpClear  = "clear"
	OpSubmit = "submit"
)

type pendingOp struct {
	kind  string
	value string
}

// defaultHistoryLimit is the default maximum number of history entries.
const defaultHistoryLimit = 2000

// InputLine wraps a single-line editor widget.
// After Layout is called, Submitted and SubmitText reflect the most recently
// submitted line (if any).  The caller should read and reset them.
type InputLine struct {
	editor      widget.Editor
	Submitted   bool
	SubmitText  string
	focusedOnce bool
	everUsed    bool // true once the user has typed or submitted anything

	history      []string // submitted commands, oldest first
	historyLimit int      // maximum entries; 0 = unlimited
	histIdx      int      // current position; len(history) = "not browsing"
	savedInput   string   // text buffered before history browsing began

	pendingMu  sync.Mutex
	pendingOps []pendingOp
	hint       string
	fontName   string

	// DreamWordProvider returns the current dream word; called when Ctrl-D is pressed.
	// If nil or returns "", Ctrl-D is a no-op.
	DreamWordProvider func() string

	// FKeyProvider returns the bound command for a given modifier and F-key name.
	// mod is "none", "shift", or "ctrl"; key is "F1"-"F12".
	// If nil or returns "", the key is a no-op.
	FKeyProvider func(mod, key string) string
}

// NewInputLine returns a configured InputLine.
func NewInputLine() *InputLine {
	il := &InputLine{hint: "Type here and press Enter\u2026", fontName: defaultFontName, historyLimit: defaultHistoryLimit}
	il.editor.SingleLine = true
	il.editor.Submit = true
	return il
}

// SetHistoryLimit sets the maximum number of history entries to keep.
// Older entries are dropped when the limit is exceeded. A value of 0 disables
// the limit (unbounded).
func (il *InputLine) SetHistoryLimit(n int) { il.historyLimit = n }

// SetFont sets the typeface used in the editor.
func (il *InputLine) SetFont(name string) { il.fontName = name }

// SetText sets the editor text (main goroutine only).
func (il *InputLine) SetText(s string) { il.editor.SetText(s) }

// Clear clears the editor (main goroutine only).
func (il *InputLine) Clear() { il.editor.SetText("") }

// Hint returns the current placeholder hint text.
func (il *InputLine) Hint() string { return il.hint }

// SetHint changes the placeholder hint text (main goroutine only).
func (il *InputLine) SetHint(s string) { il.hint = s }

// EnqueueOp thread-safely enqueues an operation to be applied during the next Layout call.
func (il *InputLine) EnqueueOp(kind, value string) {
	il.pendingMu.Lock()
	il.pendingOps = append(il.pendingOps, pendingOp{kind, value})
	il.pendingMu.Unlock()
}

// drainOps applies all pending operations to the editor.
// Must be called from the main goroutine (during Layout).
func (il *InputLine) drainOps() {
	il.pendingMu.Lock()
	ops := il.pendingOps
	il.pendingOps = nil
	il.pendingMu.Unlock()
	for _, op := range ops {
		switch op.kind {
		case OpText:
			il.editor.SetText(il.editor.Text() + op.value)
		case OpBS:
			t := []rune(il.editor.Text())
			if len(t) > 0 {
				il.editor.SetText(string(t[:len(t)-1]))
			}
		case OpClear:
			il.editor.SetText("")
		case OpSubmit:
			il.SubmitText = il.editor.Text()
			il.Submitted = true
			il.everUsed = true
			il.appendHistory(il.SubmitText)
			il.editor.SetText("")
		}
	}
}

// appendHistory adds text to history, skipping exact consecutive duplicates.
func (il *InputLine) appendHistory(text string) {
	if text == "" {
		return
	}
	if len(il.history) == 0 || il.history[len(il.history)-1] != text {
		il.history = append(il.history, text)
		if il.historyLimit > 0 && len(il.history) > il.historyLimit {
			excess := len(il.history) - il.historyLimit
			copy(il.history, il.history[excess:])
			il.history = il.history[:il.historyLimit]
			il.histIdx -= excess
			if il.histIdx < 0 {
				il.histIdx = 0
			}
		}
	}
	il.histIdx = len(il.history)
	il.savedInput = ""
}

// historyUp navigates to the previous command in history.
func (il *InputLine) historyUp() {
	if il.histIdx == len(il.history) {
		il.savedInput = il.editor.Text()
	}
	if il.histIdx > 0 {
		il.histIdx--
		t := il.history[il.histIdx]
		il.editor.SetText(t)
		il.editor.SetCaret(len(t), len(t))
	}
}

// historyDown navigates to the next (newer) command in history.
func (il *InputLine) historyDown() {
	if il.histIdx >= len(il.history) {
		return
	}
	il.histIdx++
	if il.histIdx == len(il.history) {
		t := il.savedInput
		il.editor.SetText(t)
		il.editor.SetCaret(len(t), len(t))
	} else {
		t := il.history[il.histIdx]
		il.editor.SetText(t)
		il.editor.SetCaret(len(t), len(t))
	}
}

// Layout renders the input line and processes pending editor events.
func (il *InputLine) Layout(gtx layout.Context, th *material.Theme) layout.Dimensions {
	// Request focus on the very first frame so the editor is ready to type immediately.
	if !il.focusedOnce {
		gtx.Execute(key.FocusCmd{Tag: &il.editor})
		il.focusedOnce = true
	}

	// Reset submission state, drain pending ops, then process real editor events.
	// If both a pending OpSubmit and a real SubmitEvent fire in the same frame,
	// the last one wins — that is acceptable.
	il.Submitted = false
	il.SubmitText = ""
	il.drainOps()

	// Register the editor tag in the ops tree every frame so that key events
	// (including Ctrl-V) are routed to our filter. This must happen before the
	// filter is polled; the editor's own event.Op fires later inside ed.Layout.
	{
		area := clip.Rect{Max: gtx.Constraints.Max}.Push(gtx.Ops)
		event.Op(gtx.Ops, &il.editor)
		area.Pop()
	}

	// Intercept Ctrl-V before the editor can handle it so our sanitised paste
	// path is used instead of the editor's built-in (which replaces \n with spaces).
	for {
		e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: "V", Required: key.ModCtrl})
		if !ok {
			break
		}
		if ke, ok := e.(key.Event); ok && ke.State == key.Press {
			gtx.Execute(clipboard.ReadCmd{Tag: &il.editor})
		}
	}

	// Intercept Up/Down arrow keys for command history navigation.
	for {
		e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: key.NameUpArrow})
		if !ok {
			break
		}
		if ke, ok := e.(key.Event); ok && ke.State == key.Press {
			il.historyUp()
		}
	}
	for {
		e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: key.NameDownArrow})
		if !ok {
			break
		}
		if ke, ok := e.(key.Event); ok && ke.State == key.Press {
			il.historyDown()
		}
	}

	// Intercept Ctrl-D: speak the current dream word.
	for {
		e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: "D", Required: key.ModCtrl})
		if !ok {
			break
		}
		if ke, ok := e.(key.Event); ok && ke.State == key.Press {
			if il.DreamWordProvider != nil {
				if word := il.DreamWordProvider(); word != "" {
					cmd := fmt.Sprintf(`say "%s"`, word)
					il.SubmitText = cmd
					il.Submitted = true
					il.everUsed = true
					il.appendHistory(cmd)
					il.editor.SetText("")
				}
			}
		}
	}

	// Intercept Escape: clear the input line.
	for {
		e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: key.NameEscape})
		if !ok {
			break
		}
		if ke, ok := e.(key.Event); ok && ke.State == key.Press {
			il.editor.SetText("")
		}
	}

	// Intercept F1-F12 (unmodified, shift, ctrl) for bound command dispatch.
	fkeyNames := [12]string{"F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"}
	for _, name := range fkeyNames {
		for {
			e, ok := gtx.Event(key.Filter{Focus: &il.editor, Name: key.Name(name), Optional: key.ModShift | key.ModCtrl})
			if !ok {
				break
			}
			if ke, ok := e.(key.Event); ok && ke.State == key.Press && il.FKeyProvider != nil {
				var mod string
				switch {
				case ke.Modifiers.Contain(key.ModShift):
					mod = "shift"
				case ke.Modifiers.Contain(key.ModCtrl):
					mod = "ctrl"
				default:
					mod = "none"
				}
				if cmd := il.FKeyProvider(mod, name); cmd != "" {
					il.SubmitText = cmd
					il.Submitted = true
					il.everUsed = true
					il.appendHistory(cmd)
					il.editor.SetText("")
				}
			}
		}
	}

	// Intercept incoming clipboard data before the editor processes it so we
	// can sanitise the text (truncate at first \r or \n — MUD input is single-line only).
	for {
		e, ok := gtx.Event(transfer.TargetFilter{Target: &il.editor, Type: "application/text"})
		if !ok {
			break
		}
		if de, ok := e.(transfer.DataEvent); ok {
			rc := de.Open()
			raw, _ := io.ReadAll(rc)
			rc.Close()
			il.editor.Insert(sanitizeClipboardText(string(raw)))
		}
	}

	for {
		ev, ok := il.editor.Update(gtx)
		if !ok {
			break
		}
		if _, isSubmit := ev.(widget.SubmitEvent); isSubmit {
			il.SubmitText = il.editor.Text()
			il.Submitted = true
			il.everUsed = true
			il.appendHistory(il.SubmitText)
			il.editor.SetText("")
		}
	}

	// Mark as used once the user has typed anything.
	if !il.everUsed && il.editor.Text() != "" {
		il.everUsed = true
	}

	bg := color.NRGBA{R: 0x14, G: 0x14, B: 0x14, A: 255} // slightly lifted from panel
	paint.FillShape(gtx.Ops, bg, clip.Rect{Max: gtx.Constraints.Max}.Op())

	return layout.UniformInset(unit.Dp(4)).Layout(gtx, func(gtx layout.Context) layout.Dimensions {
		hint := il.hint
		if il.everUsed {
			hint = ""
		}
		ed := material.Editor(th, &il.editor, hint)
		ed.Font.Typeface = font.Typeface(il.fontName)
		ed.Color = color.NRGBA{R: 220, G: 220, B: 220, A: 255}
		ed.HintColor = color.NRGBA{R: 160, G: 160, B: 160, A: 255}
		return ed.Layout(gtx)
	})
}

// sanitizeClipboardText truncates pasted text at the first carriage return or
// newline. MUD input is single-line; everything after the first line break is
// discarded to avoid corrupting the command buffer.
func sanitizeClipboardText(s string) string {
	if i := strings.IndexAny(s, "\r\n"); i >= 0 {
		return s[:i]
	}
	return s
}
