package ui

import (
	"gioui.org/layout"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/fes"
)

// UI holds all top-level UI state for mucka.
type UI struct {
	StatusBar  *StatusBar
	TextPanel  *TextPanel
	InputLine  *InputLine
	// OnSubmit is called whenever the user submits a line of input.
	// If nil, submitted text is echoed to the TextPanel.
	OnSubmit   func(text string)
	// ConnStatus returns the current connection state for the status bar spinner.
	ConnStatus func() (connecting, connected bool)
}

// New creates a UI with all sub-widgets initialised.
func New() *UI {
	return &UI{
		StatusBar: NewStatusBar(),
		TextPanel: NewTextPanel(),
		InputLine: NewInputLine(),
	}
}

// SetFont propagates the typeface name to all three sub-widgets.
func (u *UI) SetFont(name string) {
	u.TextPanel.SetFont(name)
	u.InputLine.SetFont(name)
	u.StatusBar.SetFont(name)
}

// SetStats forwards live character stats to the status bar for display.
// Safe to call from any goroutine.
func (u *UI) SetStats(s *fes.Stats) {
	u.StatusBar.SetStats(s)
}

// SetDreamWord forwards the current dream word to the status bar.
// Empty string clears the display. Safe to call from any goroutine.
func (u *UI) SetDreamWord(word string) {
	u.StatusBar.SetDreamWord(word)
}

// Layout renders the 3-zone layout:
//
//	┌─────────────────────┐
//	│     status bar      │  (Rigid top)
//	├─────────────────────┤
//	│   scrollable text   │  (Flexed middle)
//	├─────────────────────┤
//	│     input line      │  (Rigid bottom)
//	└─────────────────────┘
//
// It also handles input submission: submitted text is echoed to the text
// panel and cleared from the input line.
func (u *UI) Layout(gtx layout.Context, th *material.Theme) layout.Dimensions {
	return layout.Flex{Axis: layout.Vertical}.Layout(gtx,
		layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			var connecting, connected bool
			if u.ConnStatus != nil {
				connecting, connected = u.ConnStatus()
			}
			return u.StatusBar.Layout(gtx, th, connecting, connected)
		}),
		layout.Flexed(1, func(gtx layout.Context) layout.Dimensions {
			return u.TextPanel.Layout(gtx, th)
		}),
		layout.Rigid(func(gtx layout.Context) layout.Dimensions {
			dims := u.InputLine.Layout(gtx, th)
			if u.InputLine.Submitted {
				text := u.InputLine.SubmitText
				if u.OnSubmit != nil {
					u.OnSubmit(text)
				} else if text != "" {
					u.TextPanel.AppendText(text)
				}
			}
			return dims
		}),
	)
}
