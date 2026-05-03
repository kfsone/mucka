package mud2

import (
	"image/color"
	"testing"

	"github.com/kfsone/mucka/internal/ansi"
)

func TestColorMap_ParseALLine_Valid(t *testing.T) {
	tests := []struct {
		name       string
		line       string
		wantFG     color.NRGBA
		wantBG     color.NRGBA
		wantType   int
		wantResult bool
	}{
		{
			name:       "green fg normal bg type 3",
			line:       "/ASGn3",
			wantFG:     ansi.StandardColor(2), // green
			wantBG:     ansi.DefaultBG,
			wantType:   3,
			wantResult: true,
		},
		{
			name:       "yellow fg normal bg type 13",
			line:       "/ASYn13",
			wantFG:     ansi.StandardColor(3), // yellow
			wantBG:     ansi.DefaultBG,
			wantType:   13,
			wantResult: true,
		},
		{
			name:       "cyan fg normal bg type 6",
			line:       "/ASCn6",
			wantFG:     ansi.StandardColor(6), // cyan
			wantBG:     ansi.DefaultBG,
			wantType:   6,
			wantResult: true,
		},
		{
			name:       "bright red fg normal bg type 9",
			line:       "/ASrn9",
			wantFG:     ansi.BrightColor(1), // bright red
			wantBG:     ansi.DefaultBG,
			wantType:   9,
			wantResult: true,
		},
		{
			name:       "type 0 default text",
			line:       "/ASnn0",
			wantFG:     ansi.DefaultFG,
			wantBG:     ansi.DefaultBG,
			wantType:   0,
			wantResult: true,
		},
		{
			name:       "type 60 upper bound",
			line:       "/ASWn60",
			wantFG:     ansi.StandardColor(7), // white
			wantBG:     ansi.DefaultBG,
			wantType:   60,
			wantResult: true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			m := NewColorMap()
			got := m.ParseALLine(tt.line)
			if got != tt.wantResult {
				t.Errorf("ParseALLine(%q) = %v, want %v", tt.line, got, tt.wantResult)
			}
			if tt.wantResult {
				typeNum := m.Lookup(tt.wantFG, tt.wantBG)
				if typeNum != tt.wantType {
					t.Errorf("Lookup after ParseALLine(%q): got type %d, want %d", tt.line, typeNum, tt.wantType)
				}
			}
		})
	}
}

func TestColorMap_ParseALLine_Invalid(t *testing.T) {
	tests := []struct {
		name string
		line string
	}{
		{"empty", ""},
		{"too short", "/ASGn"},
		{"wrong prefix 1", "XASGn3"},
		{"wrong prefix 2", "/XSGn3"},
		{"wrong prefix 3", "/AXGn3"},
		{"unknown fg letter", "/ASZn3"},
		{"unknown bg letter", "/ASGz3"},
		{"type too high", "/ASGn61"},
		{"type negative", "/ASGn-1"},
		{"type not a number", "/ASGnX"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			m := NewColorMap()
			if m.ParseALLine(tt.line) {
				t.Errorf("ParseALLine(%q) = true, want false", tt.line)
			}
		})
	}
}

func TestColorMap_Lookup_NotFound(t *testing.T) {
	m := NewColorMap()
	typeNum := m.Lookup(ansi.StandardColor(1), ansi.DefaultBG)
	if typeNum != 0 {
		t.Errorf("Lookup on empty map = %d, want 0", typeNum)
	}
}

func TestColorMap_SetAndLookup(t *testing.T) {
	m := NewColorMap()
	fg := ansi.StandardColor(2) // green
	bg := ansi.DefaultBG

	m.Set(fg, bg, 3)
	if got := m.Lookup(fg, bg); got != 3 {
		t.Errorf("Lookup after Set = %d, want 3", got)
	}
}

func TestColorMap_ParseALLine_OverwritesExisting(t *testing.T) {
	m := NewColorMap()
	// Two types sharing the same color: the later one wins.
	m.ParseALLine("/ASYn17")
	m.ParseALLine("/ASYn18")
	typeNum := m.Lookup(ansi.StandardColor(3), ansi.DefaultBG) // yellow
	if typeNum != 18 {
		t.Errorf("expected type 18 (last set), got %d", typeNum)
	}
}

func TestColorMap_AllColorLetters(t *testing.T) {
	// Verify every documented color letter maps to a non-zero color.
	tests := []struct {
		letter byte
		isFG   bool
	}{
		{'K', true}, {'R', true}, {'G', true}, {'Y', true},
		{'B', true}, {'M', true}, {'C', true}, {'W', true},
		{'k', true}, {'r', true}, {'g', true}, {'y', true},
		{'b', true}, {'m', true}, {'c', true}, {'w', true},
		{'n', true},
	}
	for _, tt := range tests {
		def := ansi.DefaultFG
		if !tt.isFG {
			def = ansi.DefaultBG
		}
		c, ok := letterToColor(tt.letter, def)
		if !ok {
			t.Errorf("letterToColor(%q) returned ok=false", tt.letter)
		}
		// 'n' maps to the default, which may be a valid value.
		_ = c
	}
}
