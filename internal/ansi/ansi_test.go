package ansi

import (
	"image/color"
	"testing"
)

func TestParseEmpty(t *testing.T) {
	spans := Parse("")
	if len(spans) != 0 {
		t.Errorf("expected 0 spans, got %d", len(spans))
	}
}

func TestParsePlainText(t *testing.T) {
	spans := Parse("hello world")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].Text != "hello world" {
		t.Errorf("text mismatch: %q", spans[0].Text)
	}
	if spans[0].FG != DefaultFG {
		t.Errorf("FG mismatch")
	}
	if spans[0].BG != DefaultBG {
		t.Errorf("BG mismatch")
	}
	if spans[0].Bold {
		t.Errorf("expected not bold")
	}
}

func TestParseReset(t *testing.T) {
	// ESC[0m should reset to defaults
	spans := Parse("\x1b[31mred\x1b[0mnormal")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d: %+v", len(spans), spans)
	}
	if spans[0].Text != "red" {
		t.Errorf("span0 text: %q", spans[0].Text)
	}
	if spans[0].FG != standardColors[1] {
		t.Errorf("span0 FG should be red")
	}
	if spans[1].Text != "normal" {
		t.Errorf("span1 text: %q", spans[1].Text)
	}
	if spans[1].FG != DefaultFG {
		t.Errorf("span1 FG should be DefaultFG after reset")
	}
}

func TestParseResetBare(t *testing.T) {
	// ESC[m (no params) == ESC[0m
	spans := Parse("\x1b[32mgreen\x1b[mnormal")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d", len(spans))
	}
	if spans[1].FG != DefaultFG {
		t.Errorf("expected DefaultFG after bare reset")
	}
}

func TestParseBold(t *testing.T) {
	spans := Parse("\x1b[1mbold text")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if !spans[0].Bold {
		t.Errorf("expected bold=true")
	}
}

func TestParseStandardFGColors(t *testing.T) {
	tests := []struct {
		code  int
		color color.NRGBA
	}{
		{30, standardColors[0]}, // black
		{31, standardColors[1]}, // red
		{32, standardColors[2]}, // green
		{33, standardColors[3]}, // yellow
		{34, standardColors[4]}, // blue
		{35, standardColors[5]}, // magenta
		{36, standardColors[6]}, // cyan
		{37, standardColors[7]}, // white
	}
	for _, tt := range tests {
		input := string([]byte{'\x1b', '[', byte('0' + tt.code/10), byte('0' + tt.code%10), 'm'}) + "x"
		spans := Parse(input)
		if len(spans) == 0 {
			t.Errorf("code %d: no spans", tt.code)
			continue
		}
		if spans[0].FG != tt.color {
			t.Errorf("code %d: FG %v, want %v", tt.code, spans[0].FG, tt.color)
		}
	}
}

func TestParseStandardBGColors(t *testing.T) {
	tests := []struct {
		code  int
		color color.NRGBA
	}{
		{40, standardColors[0]},
		{41, standardColors[1]},
		{47, standardColors[7]},
	}
	for _, tt := range tests {
		input := string([]byte{'\x1b', '[', byte('0' + tt.code/10), byte('0' + tt.code%10), 'm'}) + "x"
		spans := Parse(input)
		if len(spans) == 0 {
			t.Errorf("code %d: no spans", tt.code)
			continue
		}
		if spans[0].BG != tt.color {
			t.Errorf("code %d: BG %v, want %v", tt.code, spans[0].BG, tt.color)
		}
	}
}

func TestParseBrightFGColors(t *testing.T) {
	// 90 = bright black (dark gray)
	spans := Parse("\x1b[90mbright black\x1b[97mbright white")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d", len(spans))
	}
	if spans[0].FG != brightColors[0] {
		t.Errorf("span0 FG should be bright black: %v", spans[0].FG)
	}
	if spans[1].FG != brightColors[7] {
		t.Errorf("span1 FG should be bright white: %v", spans[1].FG)
	}
}

func TestParseBrightBGColors(t *testing.T) {
	spans := Parse("\x1b[100mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].BG != brightColors[0] {
		t.Errorf("BG should be bright black: %v", spans[0].BG)
	}
}

func TestParse256ColorFG(t *testing.T) {
	// 38;5;196 = cube index 196 = red in 256-color
	spans := Parse("\x1b[38;5;196mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	// Index 196: (196-16) = 180, r=180/36=5, g=(180/6)%6=0 ... wait let me calc
	// 196-16=180, r=180/36=5, g=(180%36)/6=0, b=180%6=0 -> R=255+40*(5-1)=255? no
	// cubeStep(5) = 95+40*4 = 255; cubeStep(0)=0
	expected := color.NRGBA{R: 255, G: 0, B: 0, A: 0xFF}
	if spans[0].FG != expected {
		t.Errorf("256-color FG: got %v, want %v", spans[0].FG, expected)
	}
}

func TestParse256ColorBG(t *testing.T) {
	// 48;5;21 = cube index 21 = pure blue
	// 21-16=5, r=0, g=0, b=5
	spans := Parse("\x1b[48;5;21mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	expected := color.NRGBA{R: 0, G: 0, B: 255, A: 0xFF}
	if spans[0].BG != expected {
		t.Errorf("256-color BG: got %v, want %v", spans[0].BG, expected)
	}
}

func TestParse256ColorGrayscale(t *testing.T) {
	// 38;5;232 = first grayscale = 8,8,8
	spans := Parse("\x1b[38;5;232mx")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	expected := color.NRGBA{R: 8, G: 8, B: 8, A: 0xFF}
	if spans[0].FG != expected {
		t.Errorf("grayscale FG: got %v, want %v", spans[0].FG, expected)
	}
}

func TestParseNonSGRSequenceIgnored(t *testing.T) {
	// ESC[2J is a non-SGR CSI sequence — should be ignored (no text emitted).
	spans := Parse("before\x1b[2Jafter")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d: %+v", len(spans), spans)
	}
	if spans[0].Text != "before" || spans[1].Text != "after" {
		t.Errorf("unexpected spans: %+v", spans)
	}
}

func TestParseMultipleParamsOneLine(t *testing.T) {
	// ESC[1;32m = bold + green; bold promotes green (index 2) to bright green
	spans := Parse("\x1b[1;32mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if !spans[0].Bold {
		t.Errorf("expected bold")
	}
	if spans[0].FG != brightColors[2] {
		t.Errorf("expected bright green FG (bold promotion), got %v", spans[0].FG)
	}
}

func TestParse256ColorIndexLow(t *testing.T) {
	// 38;5;0 = standard color 0 (black)
	spans := Parse("\x1b[38;5;0mx")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span")
	}
	if spans[0].FG != standardColors[0] {
		t.Errorf("expected standard black, got %v", spans[0].FG)
	}
}

func TestParse256ColorIndexBright(t *testing.T) {
	// 38;5;8 = bright color 0 (dark gray)
	spans := Parse("\x1b[38;5;8mx")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span")
	}
	if spans[0].FG != brightColors[0] {
		t.Errorf("expected bright black, got %v", spans[0].FG)
	}
}

// TestParseMultipleConsecutiveCodes verifies that stacked escape sequences
// (e.g. bold + fg + bg with no intervening text) all apply to the following span.
func TestParseMultipleConsecutiveCodes(t *testing.T) {
	// ESC[1m ESC[31m ESC[44m applied together, then "text"
	// bold promotes red (index 1) to bright red
	spans := Parse("\x1b[1m\x1b[31m\x1b[44mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d: %+v", len(spans), spans)
	}
	if !spans[0].Bold {
		t.Error("expected bold")
	}
	if spans[0].FG != brightColors[1] {
		t.Errorf("expected bright red FG (bold promotion), got %v", spans[0].FG)
	}
	if spans[0].BG != standardColors[4] {
		t.Errorf("expected blue BG, got %v", spans[0].BG)
	}
}

// TestParseUnterminatedESCAtEnd verifies a lone ESC at end-of-string is silently dropped.
func TestParseUnterminatedESCAtEnd(t *testing.T) {
	spans := Parse("hello\x1b")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].Text != "hello" {
		t.Errorf("unexpected text: %q", spans[0].Text)
	}
}

// TestParseUnterminatedCSI verifies that a CSI sequence with no final byte discards
// the partial sequence and does not emit text.
func TestParseUnterminatedCSI(t *testing.T) {
	spans := Parse("before\x1b[31")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span for 'before', got %d: %+v", len(spans), spans)
	}
	if spans[0].Text != "before" {
		t.Errorf("unexpected text: %q", spans[0].Text)
	}
}

// TestParseNonCSIEscape verifies that ESC followed by a non-'[' byte is silently dropped.
func TestParseNonCSIEscape(t *testing.T) {
	spans := Parse("a\x1bBb")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d: %+v", len(spans), spans)
	}
	if spans[0].Text != "a" || spans[1].Text != "b" {
		t.Errorf("unexpected texts: %q %q", spans[0].Text, spans[1].Text)
	}
}

// TestParseTrailingCodeNoText verifies that an ANSI code at the end of the string
// (with no following text) produces no additional span.
func TestParseTrailingCodeNoText(t *testing.T) {
	spans := Parse("hello\x1b[31m")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d: %+v", len(spans), spans)
	}
	if spans[0].Text != "hello" {
		t.Errorf("unexpected text: %q", spans[0].Text)
	}
}

// TestParse256MissingSubParam verifies that a 38;5 with no third param is a no-op.
func TestParse256MissingSubParam(t *testing.T) {
	// "38;5" alone — no index — should not change fg and must not panic.
	spans := Parse("\x1b[38;5mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	// FG should remain DefaultFG (the 38;5 with no index is silently ignored).
	if spans[0].FG != DefaultFG {
		t.Errorf("expected DefaultFG, got %v", spans[0].FG)
	}
}

// TestParse256OutOfRange verifies that an out-of-range 256-color index falls back to DefaultFG.
func TestParse256OutOfRange(t *testing.T) {
	spans := Parse("\x1b[38;5;256mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != DefaultFG {
		t.Errorf("expected DefaultFG for out-of-range index, got %v", spans[0].FG)
	}
}

// TestParseBrightBGFullRange spot-checks each bright BG code 100-107.
func TestParseBrightBGFullRange(t *testing.T) {
	for i := 0; i < 8; i++ {
		code := 100 + i
		input := string([]byte{'\x1b', '[', '1', '0', byte('0' + i), 'm'}) + "x"
		spans := Parse(input)
		if len(spans) == 0 {
			t.Errorf("code %d: no spans", code)
			continue
		}
		if spans[0].BG != brightColors[i] {
			t.Errorf("code %d: BG %v, want %v", code, spans[0].BG, brightColors[i])
		}
	}
}

// TestParseResetMidSequence verifies that ESC[0m in the middle resets bold and colors,
// and the state after reset is carried into subsequent text.
func TestParseResetMidSequence(t *testing.T) {
	spans := Parse("\x1b[1;31mbold-red\x1b[0mnormal\x1b[32mgreen")
	if len(spans) != 3 {
		t.Fatalf("expected 3 spans, got %d: %+v", len(spans), spans)
	}
	// bold promotes red (index 1) to bright red
	if !spans[0].Bold || spans[0].FG != brightColors[1] {
		t.Errorf("span0 should be bold bright-red: %+v", spans[0])
	}
	if spans[1].Bold || spans[1].FG != DefaultFG {
		t.Errorf("span1 should be normal after reset: %+v", spans[1])
	}
	if spans[2].FG != standardColors[2] {
		t.Errorf("span2 should be green: %+v", spans[2])
	}
}

// TestParseStatefulCarriesColor verifies that color set on line 1 persists to line 2.
func TestParseStatefulCarriesColor(t *testing.T) {
	var st State

	// Line 1 sets green FG; line 2 has no escape codes.
	spans1 := ParseStateful("\x1b[32mgreen text", &st)
	spans2 := ParseStateful("still green", &st)

	if len(spans1) != 1 || spans1[0].FG != standardColors[2] {
		t.Errorf("line1: expected green span, got %+v", spans1)
	}
	if len(spans2) != 1 || spans2[0].FG != standardColors[2] {
		t.Errorf("line2: expected green carried over, got %+v", spans2)
	}
}

// TestParseStatefulResetClearsState verifies that ESC[0m resets state for the next call.
func TestParseStatefulResetClearsState(t *testing.T) {
	var st State

	ParseStateful("\x1b[32mgreen", &st)
	// Reset at end of line 1.
	ParseStateful("\x1b[0m", &st)
	spans := ParseStateful("normal", &st)

	if len(spans) != 1 || spans[0].FG != DefaultFG {
		t.Errorf("expected DefaultFG after reset, got %+v", spans)
	}
}

// TestParseStatelessResetsEachCall verifies that Parse() always starts from defaults.
func TestParseStatelessResetsEachCall(t *testing.T) {
	// Call 1 sets green; call 2 should not inherit it.
	Parse("\x1b[32mgreen")
	spans := Parse("should be default")
	if len(spans) != 1 || spans[0].FG != DefaultFG {
		t.Errorf("Parse should reset to DefaultFG each call, got %+v", spans)
	}
}

// TestParseStatefulBoldCarries verifies bold persists across calls.
func TestParseStatefulBoldCarries(t *testing.T) {
	var st State
	ParseStateful("\x1b[1mbold", &st)
	spans := ParseStateful("still bold", &st)
	if len(spans) != 1 || !spans[0].Bold {
		t.Errorf("bold should carry across calls, got %+v", spans)
	}
}

// TestCampbellPaletteStandardExact verifies all 8 standard (SGR 30-37) Campbell colors
// against their canonical hex values.
func TestCampbellPaletteStandardExact(t *testing.T) {
	want := [8]color.NRGBA{
		{R: 0x0C, G: 0x0C, B: 0x0C, A: 0xFF}, // 0 black   #0C0C0C
		{R: 0xC5, G: 0x0F, B: 0x1F, A: 0xFF}, // 1 red     #C50F1F
		{R: 0x13, G: 0xA1, B: 0x0E, A: 0xFF}, // 2 green   #13A10E
		{R: 0xC1, G: 0x9C, B: 0x00, A: 0xFF}, // 3 yellow  #C19C00
		{R: 0x00, G: 0x37, B: 0xDA, A: 0xFF}, // 4 blue    #0037DA
		{R: 0x88, G: 0x17, B: 0x98, A: 0xFF}, // 5 magenta #881798
		{R: 0x3A, G: 0x96, B: 0xDD, A: 0xFF}, // 6 cyan    #3A96DD
		{R: 0xCC, G: 0xCC, B: 0xCC, A: 0xFF}, // 7 white   #CCCCCC
	}
	for i, c := range want {
		if standardColors[i] != c {
			t.Errorf("standardColors[%d] = %v, want %v", i, standardColors[i], c)
		}
	}
}

// TestCampbellPaletteBrightExact verifies all 8 bright (SGR 90-97) Campbell colors.
func TestCampbellPaletteBrightExact(t *testing.T) {
	want := [8]color.NRGBA{
		{R: 0x76, G: 0x76, B: 0x76, A: 0xFF}, // 0 bright black   #767676
		{R: 0xE7, G: 0x48, B: 0x56, A: 0xFF}, // 1 bright red     #E74856
		{R: 0x16, G: 0xC6, B: 0x0C, A: 0xFF}, // 2 bright green   #16C60C
		{R: 0xF9, G: 0xF1, B: 0xA5, A: 0xFF}, // 3 bright yellow  #F9F1A5
		{R: 0x3B, G: 0x78, B: 0xFF, A: 0xFF}, // 4 bright blue    #3B78FF
		{R: 0xB4, G: 0x00, B: 0x9E, A: 0xFF}, // 5 bright magenta #B4009E
		{R: 0x61, G: 0xD6, B: 0xD6, A: 0xFF}, // 6 bright cyan    #61D6D6
		{R: 0xF2, G: 0xF2, B: 0xF2, A: 0xFF}, // 7 bright white   #F2F2F2
	}
	for i, c := range want {
		if brightColors[i] != c {
			t.Errorf("brightColors[%d] = %v, want %v", i, brightColors[i], c)
		}
	}
}

// TestDefaultFGExact verifies DefaultFG is Campbell grey #CCCCCC.
func TestDefaultFGExact(t *testing.T) {
	want := color.NRGBA{R: 0xCC, G: 0xCC, B: 0xCC, A: 0xFF}
	if DefaultFG != want {
		t.Errorf("DefaultFG = %v, want %v", DefaultFG, want)
	}
}

// TestParseStatefulCarriesColorThreeCalls verifies color persists across 3+ calls.
func TestParseStatefulCarriesColorThreeCalls(t *testing.T) {
	var st State

	ParseStateful("\x1b[34mblue text", &st) // line 1: set blue
	ParseStateful("still blue", &st)         // line 2: no escape
	spans := ParseStateful("also blue", &st) // line 3: must still be blue

	if len(spans) != 1 {
		t.Fatalf("line3: expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != standardColors[4] {
		t.Errorf("line3: expected blue (Campbell #0037DA), got %v", spans[0].FG)
	}
}

// TestParseStatefulZeroState verifies that a zero-value State is correctly
// initialised on first use (initialised field defaults to false → ensure() seeds defaults).
func TestParseStatefulZeroState(t *testing.T) {
	var st State // zero value, not explicitly initialised

	spans := ParseStateful("plain", &st)
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != DefaultFG {
		t.Errorf("zero State should start with DefaultFG, got %v", spans[0].FG)
	}
	if spans[0].Bold {
		t.Errorf("zero State should start non-bold")
	}
}

// TestSGR_BrightFG verifies SGR 91 (bright red) sets FG to palette index 9 (brightColors[1]).
func TestSGR_BrightFG(t *testing.T) {
	spans := Parse("\x1b[91mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != brightColors[1] {
		t.Errorf("SGR 91: want brightColors[1] (index 9), got %v", spans[0].FG)
	}
}

// TestSGR_BrightBG verifies SGR 101 (bright red BG) sets BG to palette index 9 (brightColors[1]).
func TestSGR_BrightBG(t *testing.T) {
	spans := Parse("\x1b[101mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].BG != brightColors[1] {
		t.Errorf("SGR 101: want brightColors[1] (index 9), got %v", spans[0].BG)
	}
}

// TestSGR_BoldPromotesFG verifies that bold + standard FG promotes to the bright variant.
func TestSGR_BoldPromotesFG(t *testing.T) {
	// \e[1;34m = bold + blue (index 4) → promoted to bright blue (index 12 = brightColors[4])
	spans := Parse("\x1b[1;34mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != brightColors[4] {
		t.Errorf("bold+blue: want brightColors[4] (index 12), got %v", spans[0].FG)
	}
}

// TestSGR_BoldReset verifies that after ESC[0m reset, re-applied FG without bold is not promoted.
func TestSGR_BoldReset(t *testing.T) {
	// span0: bold+blue → bright blue; span1: reset+blue → dark blue (no bold, no promotion)
	spans := Parse("\x1b[1;34mA\x1b[0m\x1b[34mB")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d: %+v", len(spans), spans)
	}
	if spans[0].FG != brightColors[4] {
		t.Errorf("span0 bold+blue: want brightColors[4], got %v", spans[0].FG)
	}
	if spans[1].FG != standardColors[4] {
		t.Errorf("span1 no-bold+blue: want standardColors[4] (no promotion), got %v", spans[1].FG)
	}
}

// TestSGR_BrightFGEdge verifies the edges of the 90–97 range.
func TestSGR_BrightFGEdge(t *testing.T) {
	spans90 := Parse("\x1b[90mx")
	if len(spans90) != 1 || spans90[0].FG != brightColors[0] {
		t.Errorf("SGR 90: want brightColors[0] (index 8), got %v", spans90[0].FG)
	}
	spans97 := Parse("\x1b[97mx")
	if len(spans97) != 1 || spans97[0].FG != brightColors[7] {
		t.Errorf("SGR 97: want brightColors[7] (index 15), got %v", spans97[0].FG)
	}
}

// TestSGR_BrightBGEdge verifies the edges of the 100–107 range.
func TestSGR_BrightBGEdge(t *testing.T) {
	spans100 := Parse("\x1b[100mx")
	if len(spans100) != 1 || spans100[0].BG != brightColors[0] {
		t.Errorf("SGR 100: want brightColors[0] (index 8), got %v", spans100[0].BG)
	}
	spans107 := Parse("\x1b[107mx")
	if len(spans107) != 1 || spans107[0].BG != brightColors[7] {
		t.Errorf("SGR 107: want brightColors[7] (index 15), got %v", spans107[0].BG)
	}
}

// TestSGR22_BoldOff verifies that SGR 22 (normal intensity / bold-off) clears bold
// so that a previously-promoted FG reverts to the standard (dark) color.
// \e[1;34m sets bold+blue → promoted to bright blue; \e[22m should clear bold
// so the trailing "B" span uses standard blue (no promotion).
func TestSGR22_BoldOff(t *testing.T) {
	spans := Parse("\x1b[1;34mA\x1b[22mB")
	if len(spans) != 2 {
		t.Fatalf("expected 2 spans, got %d: %+v", len(spans), spans)
	}
	if spans[0].FG != brightColors[4] {
		t.Errorf("span0 bold+blue: want brightColors[4] (bright blue), got %v", spans[0].FG)
	}
	// After SGR 22 (bold-off), FG should revert to standard blue — no promotion.
	if spans[1].FG != standardColors[4] {
		t.Errorf("span1 after SGR 22: want standardColors[4] (dark blue, bold cleared), got %v", spans[1].FG)
	}
	if spans[1].Bold {
		t.Errorf("span1 should not be bold after SGR 22")
	}
}

// TestSGR_BoldPlusExplicitBrightNoDoublePromotion verifies that setting bold AND
// an explicit bright FG (SGR 90–97) does NOT double-promote: the explicit bright
// color must be used as-is because fgStdIdx is -1 for SGR 90–97.
func TestSGR_BoldPlusExplicitBrightNoDoublePromotion(t *testing.T) {
	// \e[1;94m = bold + explicit bright blue (SGR 94)
	// Must emit brightColors[4], not some further-promoted value.
	spans := Parse("\x1b[1;94mtext")
	if len(spans) != 1 {
		t.Fatalf("expected 1 span, got %d: %+v", len(spans), spans)
	}
	if spans[0].FG != brightColors[4] {
		t.Errorf("bold+SGR94: want brightColors[4] (no double-promotion), got %v", spans[0].FG)
	}
}

// TestParseStatefulBoldPlusFGPromotionCarries verifies that when bold + standard FG
// are set on line 1, the bold-promotion is applied on line 2 even though line 2
// contains no escape codes (fgStdIdx must be carried by State).
func TestParseStatefulBoldPlusFGPromotionCarries(t *testing.T) {
	var st State
	// Line 1: bold + blue. State stores fgStdIdx=4, bold=true, FG=standardColors[4].
	ParseStateful("\x1b[1;34mline one", &st)
	// Line 2: no escapes — must still render as bright blue due to carried state.
	spans := ParseStateful("line two", &st)
	if len(spans) != 1 {
		t.Fatalf("line2: expected 1 span, got %d", len(spans))
	}
	if spans[0].FG != brightColors[4] {
		t.Errorf("line2: expected bright blue (bold-promotion carried), got %v", spans[0].FG)
	}
	if !spans[0].Bold {
		t.Errorf("line2: bold should still be set")
	}
}
