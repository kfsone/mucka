// Package ansi parses ANSI SGR (Select Graphic Rendition) escape sequences
// and returns a flat list of styled text spans.
package ansi

import (
	"image/color"
	"strconv"
	"strings"
)

// Span is a run of text sharing a single foreground color, background color,
// and bold flag.  Text contains no escape sequences.
type Span struct {
	Text string
	FG   color.NRGBA
	BG   color.NRGBA
	Bold bool
}

// Default terminal colors — Windows Terminal "Campbell" palette.
var (
	DefaultFG = color.NRGBA{R: 0xCC, G: 0xCC, B: 0xCC, A: 0xFF} // #CCCCCC
	DefaultBG = color.NRGBA{R: 0x0C, G: 0x0C, B: 0x0C, A: 0xFF} // #0C0C0C Campbell background
)

// standardColors maps SGR codes 30-37 / 40-47 to NRGBA (Campbell palette).
var standardColors = [8]color.NRGBA{
	{R: 0x0C, G: 0x0C, B: 0x0C, A: 0xFF}, // 0 black   #0C0C0C
	{R: 0xC5, G: 0x0F, B: 0x1F, A: 0xFF}, // 1 red     #C50F1F
	{R: 0x13, G: 0xA1, B: 0x0E, A: 0xFF}, // 2 green   #13A10E
	{R: 0xC1, G: 0x9C, B: 0x00, A: 0xFF}, // 3 yellow  #C19C00
	{R: 0x00, G: 0x37, B: 0xDA, A: 0xFF}, // 4 blue    #0037DA
	{R: 0x88, G: 0x17, B: 0x98, A: 0xFF}, // 5 magenta #881798
	{R: 0x3A, G: 0x96, B: 0xDD, A: 0xFF}, // 6 cyan    #3A96DD
	{R: 0xCC, G: 0xCC, B: 0xCC, A: 0xFF}, // 7 white   #CCCCCC
}

// brightColors maps SGR codes 90-97 / 100-107 to NRGBA (Campbell palette).
var brightColors = [8]color.NRGBA{
	{R: 0x76, G: 0x76, B: 0x76, A: 0xFF}, // 0 bright black   #767676
	{R: 0xE7, G: 0x48, B: 0x56, A: 0xFF}, // 1 bright red     #E74856
	{R: 0x16, G: 0xC6, B: 0x0C, A: 0xFF}, // 2 bright green   #16C60C
	{R: 0xF9, G: 0xF1, B: 0xA5, A: 0xFF}, // 3 bright yellow  #F9F1A5
	{R: 0x3B, G: 0x78, B: 0xFF, A: 0xFF}, // 4 bright blue    #3B78FF
	{R: 0xB4, G: 0x00, B: 0x9E, A: 0xFF}, // 5 bright magenta #B4009E
	{R: 0x61, G: 0xD6, B: 0xD6, A: 0xFF}, // 6 bright cyan    #61D6D6
	{R: 0xF2, G: 0xF2, B: 0xF2, A: 0xFF}, // 7 bright white   #F2F2F2
}

// Parse splits s into Spans, resolving all ANSI SGR sequences.
// Non-SGR escape sequences are silently dropped (their introducer bytes
// are consumed but no text is emitted for them).
// State resets to defaults on every call — for cross-line color persistence,
// use ParseStateful instead.
func Parse(s string) []Span {
	spans, _, _, _, _ := parseInner(s, DefaultFG, DefaultBG, false, -1)
	return spans
}

// State holds ANSI rendering state across successive ParseStateful calls,
// allowing color and bold attributes to persist across line boundaries.
type State struct {
	FG          color.NRGBA
	BG          color.NRGBA
	Bold        bool
	fgStdIdx    int // 0–7 when FG is a standard color (SGR 30–37); -1 otherwise
	initialised bool
}

func (st *State) ensure() {
	if !st.initialised {
		st.FG, st.BG, st.Bold, st.fgStdIdx, st.initialised = DefaultFG, DefaultBG, false, -1, true
	}
}

// ParseStateful parses s using st as the initial rendering state, then writes
// the resulting fg/bg/bold back to st so they carry over to the next call.
func ParseStateful(s string, st *State) []Span {
	st.ensure()
	spans, fg, bg, bold, fgStdIdx := parseInner(s, st.FG, st.BG, st.Bold, st.fgStdIdx)
	st.FG, st.BG, st.Bold, st.fgStdIdx = fg, bg, bold, fgStdIdx
	return spans
}

// parseInner is the shared parser used by both Parse and ParseStateful.
// It returns the resulting spans together with the final fg/bg/bold/fgStdIdx state.
// fgStdIdx is 0–7 when fg is a standard SGR 30–37 color, -1 otherwise;
// it enables bold=bright promotion at span-build time.
func parseInner(s string, fg, bg color.NRGBA, bold bool, fgStdIdx int) ([]Span, color.NRGBA, color.NRGBA, bool, int) {
	var spans []Span

	i := 0
	textStart := 0

	for i < len(s) {
		if s[i] != '\x1b' {
			i++
			continue
		}

		// Flush accumulated plain text before the ESC.
		if i > textStart {
			spanFG := fg
			if bold && fgStdIdx >= 0 {
				spanFG = brightColors[fgStdIdx]
			}
			spans = append(spans, Span{Text: s[textStart:i], FG: spanFG, BG: bg, Bold: bold})
		}

		if i+1 >= len(s) {
			// Lone ESC at end — discard.
			i++
			textStart = i
			continue
		}

		if s[i+1] != '[' {
			// Not a CSI sequence — skip ESC + introducer byte.
			i += 2
			textStart = i
			continue
		}

		// CSI: ESC [ <params> <final>   final byte is in 0x40–0x7E.
		j := i + 2
		for j < len(s) && (s[j] < 0x40 || s[j] > 0x7E) {
			j++
		}
		if j >= len(s) {
			// Truncated sequence — discard rest of string.
			i = j
			textStart = i
			continue
		}

		if s[j] == 'm' {
			// SGR: apply the parameters to current state.
			fg, bg, bold, fgStdIdx = applyParams(s[i+2:j], fg, bg, bold, fgStdIdx)
		}
		// All other CSI sequences are silently ignored.

		i = j + 1
		textStart = i
	}

	if textStart < len(s) {
		spanFG := fg
		if bold && fgStdIdx >= 0 {
			spanFG = brightColors[fgStdIdx]
		}
		spans = append(spans, Span{Text: s[textStart:], FG: spanFG, BG: bg, Bold: bold})
	}

	return spans, fg, bg, bold, fgStdIdx
}

// applyParams processes a semicolon-delimited SGR parameter string and
// returns updated fg/bg/bold/fgStdIdx state.
// fgStdIdx is set to 0–7 when a standard SGR 30–37 color is applied, and
// reset to -1 on any other FG assignment or on a full reset (SGR 0).
func applyParams(params string, fg, bg color.NRGBA, bold bool, fgStdIdx int) (color.NRGBA, color.NRGBA, bool, int) {
	if params == "" {
		// ESC[m == ESC[0m: full reset.
		return DefaultFG, DefaultBG, false, -1
	}

	parts := strings.Split(params, ";")
	nums := make([]int, 0, len(parts))
	for _, p := range parts {
		if p == "" {
			nums = append(nums, 0)
		} else if n, err := strconv.Atoi(p); err == nil {
			nums = append(nums, n)
		}
	}

	i := 0
	for i < len(nums) {
		n := nums[i]
		switch {
		case n == 0:
			fg, bg, bold, fgStdIdx = DefaultFG, DefaultBG, false, -1
		case n == 1:
			bold = true
		case n == 22:
			bold = false
		case n >= 30 && n <= 37:
			fg = standardColors[n-30]
			fgStdIdx = n - 30
		case n >= 40 && n <= 47:
			bg = standardColors[n-40]
		case n >= 90 && n <= 97:
			fg = brightColors[n-90]
			fgStdIdx = -1 // explicit bright — not eligible for bold promotion
		case n >= 100 && n <= 107:
			bg = brightColors[n-100]
		case n == 38:
			// 256-color or 24-bit fg — only 38;5;n supported.
			if i+2 < len(nums) && nums[i+1] == 5 {
				fg = palette256(nums[i+2])
				fgStdIdx = -1 // 256-color — not eligible for bold promotion
				i += 2
			}
		case n == 48:
			// 256-color or 24-bit bg — only 48;5;n supported.
			if i+2 < len(nums) && nums[i+1] == 5 {
				bg = palette256(nums[i+2])
				i += 2
			}
		}
		i++
	}

	return fg, bg, bold, fgStdIdx
}

// palette256 maps an xterm 256-color index to NRGBA.
func palette256(n int) color.NRGBA {
	switch {
	case n < 0 || n > 255:
		return DefaultFG
	case n < 8:
		return standardColors[n]
	case n < 16:
		return brightColors[n-8]
	case n < 232:
		// 6×6×6 RGB cube: index 16 + 36*r + 6*g + b
		n -= 16
		r := n / 36
		g := (n / 6) % 6
		b := n % 6
		return color.NRGBA{R: cubeStep(r), G: cubeStep(g), B: cubeStep(b), A: 0xFF}
	default:
		// Grayscale ramp: 232–255 → 8, 18, 28, …, 238
		v := uint8(8 + 10*(n-232))
		return color.NRGBA{R: v, G: v, B: v, A: 0xFF}
	}
}

// cubeStep converts a 0–5 cube coordinate to an 8-bit intensity.
func cubeStep(i int) uint8 {
	if i == 0 {
		return 0
	}
	return uint8(95 + 40*(i-1))
}
