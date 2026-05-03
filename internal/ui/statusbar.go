package ui

import (
	"fmt"
	"image/color"
	"sync/atomic"
	"time"

	"gioui.org/font"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/unit"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/fes"
)

// StatusBar renders a single-line status strip at the top of the window.
type StatusBar struct {
	fontName string
	// stats holds the most recently set stats pointer (atomic, may be nil).
	stats     atomic.Pointer[fes.Stats]
	dreamWord atomic.Pointer[string]
}

// NewStatusBar returns an initialised StatusBar.
func NewStatusBar() *StatusBar {
	return &StatusBar{
		fontName: defaultFontName,
	}
}

// SetFont sets the typeface used in the status bar labels.
func (s *StatusBar) SetFont(name string) { s.fontName = name }

// SetStats stores a copy of the provided stats for display.  Passing nil
// clears the stats area.  Safe to call from any goroutine.
func (s *StatusBar) SetStats(st *fes.Stats) {
	if st == nil {
		s.stats.Store(nil)
		return
	}
	cp := *st
	s.stats.Store(&cp)
}

// SetDreamWord stores the current dream word for display.
// Empty string clears it. Safe to call from any goroutine.
func (s *StatusBar) SetDreamWord(word string) {
	if word == "" {
		s.dreamWord.Store(nil)
	} else {
		cp := word
		s.dreamWord.Store(&cp)
	}
}

// ratioColor returns a green/yellow/red color based on cur/max ratio.
// If max is 0 the ratio is treated as 100% (green).
func ratioColor(cur, max int) color.NRGBA {
	if max == 0 || cur*100/max >= 75 {
		return color.NRGBA{R: 0x00, G: 0xCC, B: 0x00, A: 0xFF}
	}
	if cur*100/max >= 40 {
		return color.NRGBA{R: 0xCC, G: 0xCC, B: 0x00, A: 0xFF}
	}
	return color.NRGBA{R: 0xCC, G: 0x00, B: 0x00, A: 0xFF}
}

// statPart is a (text, color) pair used to build the stats strip.
type statPart struct {
	text  string
	color color.NRGBA
}

var neutralColor = color.NRGBA{R: 0xCC, G: 0xFF, B: 0xCC, A: 0xFF}

// weatherLabel returns a short colored label for the given weather byte code.
// Codes match Clio: F=Sunny, C=Cloudy, R=Rain, S=Snow, O=Overcast, T=Storm, B=Blizzard.
func weatherLabel(w byte) (string, color.NRGBA) {
	switch w {
	case 'F':
		return "☀", color.NRGBA{R: 0xFF, G: 0xD7, B: 0x00, A: 0xFF} // gold
	case 'C':
		return "☁", color.NRGBA{R: 0xCC, G: 0xCC, B: 0xCC, A: 0xFF}
	case 'R':
		return "Rain", color.NRGBA{R: 0x44, G: 0x88, B: 0xFF, A: 0xFF}
	case 'S':
		return "❄", color.NRGBA{R: 0xAA, G: 0xDD, B: 0xFF, A: 0xFF}
	case 'O':
		return "Overcast", color.NRGBA{R: 0x99, G: 0x99, B: 0xAA, A: 0xFF}
	case 'T':
		return "Storm", color.NRGBA{R: 0x44, G: 0x88, B: 0xFF, A: 0xFF}
	case 'B':
		return "Bliz", color.NRGBA{R: 0xCC, G: 0xEE, B: 0xFF, A: 0xFF}
	default:
		return "", color.NRGBA{}
	}
}

// buildStatParts returns the ordered list of colored text segments for stats.
// Layout: ♥ cur/max  S cur/max  D cur/max  [M cur/max]  Npts  [Rank]
func buildStatParts(st *fes.Stats) []statPart {
	neu := neutralColor
	var parts []statPart

	// Stamina: ♥ cur/max (always shown).
	parts = append(parts,
		statPart{"♥ ", neu},
		statPart{fmt.Sprintf("%d/%d", st.Stamina, st.MaxStamina), ratioColor(st.Stamina, st.MaxStamina)},
	)

	// Strength: S cur/max (always shown as cur/max).
	parts = append(parts,
		statPart{"  S ", neu},
		statPart{fmt.Sprintf("%d/%d", st.Strength, st.MaxStrength), ratioColor(st.Strength, st.MaxStrength)},
	)

	// Dexterity: D cur/max (always shown as cur/max).
	parts = append(parts,
		statPart{"  D ", neu},
		statPart{fmt.Sprintf("%d/%d", st.Dexterity, st.MaxDexterity), ratioColor(st.Dexterity, st.MaxDexterity)},
	)

	// Magic: M cur/max — omit entirely when MaxMagic == 0.
	if st.MaxMagic > 0 {
		parts = append(parts,
			statPart{"  M ", neu},
			statPart{fmt.Sprintf("%d/%d", st.Magic, st.MaxMagic), ratioColor(st.Magic, st.MaxMagic)},
		)
	}

	// Score.
	parts = append(parts,
		statPart{"  ★ ", neu},
		statPart{fes.FormatInt(st.Score), neu},
	)

	// Rank — name only, suppress when empty.
	if st.Rank != "" {
		parts = append(parts, statPart{"  " + st.Rank, neu})
	}

	// Weather — omit when zero/unknown.
	if label, col := weatherLabel(st.Weather); label != "" {
		parts = append(parts, statPart{"  " + label, col})
	}

	return parts
}

// Layout renders the status bar into the provided constraints.
func (s *StatusBar) Layout(gtx layout.Context, th *material.Theme, connecting, connected bool) layout.Dimensions {
	bg := color.NRGBA{R: 0x14, G: 0x14, B: 0x14, A: 255}
	paint.FillShape(gtx.Ops, bg, clip.Rect{Max: gtx.Constraints.Max}.Op())

	var statusText string
	switch {
	case connecting:
		frames := []byte{'-', '\\', '|', '/'}
		frame := int(time.Now().UnixMilli()/120) % 4
		statusText = "[" + string([]byte{frames[frame]}) + "]"
		gtx.Execute(op.InvalidateCmd{At: time.Now().Add(120 * time.Millisecond)})
	case connected:
		statusText = "[+]"
	default:
		statusText = "   "
	}

	face := font.Typeface(s.fontName)

	makeLabel := func(text string, col color.NRGBA) layout.Widget {
		return func(gtx layout.Context) layout.Dimensions {
			lbl := material.Label(th, defaultFontSize, text)
			lbl.Font.Typeface = face
			lbl.Color = col
			return lbl.Layout(gtx)
		}
	}

	stats := s.stats.Load()

	return layout.UniformInset(unit.Dp(4)).Layout(gtx, func(gtx layout.Context) layout.Dimensions {
		// Build children: stat parts (left-aligned), spacer, spinner.
		var children []layout.FlexChild
		if stats != nil {
			for _, p := range buildStatParts(stats) {
				p := p // capture
				children = append(children, layout.Rigid(makeLabel(p.text, p.color)))
			}
		}
		// Spacer pushes the right-side widgets to the far right.
		children = append(children, layout.Flexed(1, func(gtx layout.Context) layout.Dimensions {
			return layout.Dimensions{Size: gtx.Constraints.Min}
		}))
		// Dream word indicator (cyan, right-aligned before reset timer).
		if dw := s.dreamWord.Load(); dw != nil {
			dreamColor := color.NRGBA{R: 0x3A, G: 0x96, B: 0xDD, A: 0xFF} // Campbell cyan
			children = append(children, layout.Rigid(makeLabel("💤 "+*dw+" ", dreamColor)))
		}
		// Reset timer (e.g. "44m ") shown when available.
		if stats != nil && stats.ResetMinutes > 0 {
			resetText := fmt.Sprintf("%dm ", stats.ResetMinutes)
			children = append(children, layout.Rigid(makeLabel(resetText, neutralColor)))
		}
		// Spinner on the right.
		children = append(children, layout.Rigid(makeLabel(statusText, neutralColor)))

		return layout.Flex{Axis: layout.Horizontal}.Layout(gtx, children...)
	})
}
