package mud2

import (
	"testing"
)

func TestIsGameEntry(t *testing.T) {
	tests := []struct {
		name string
		text string
		want bool
	}{
		{"opening area name", "Elizabethan London", true},
		{"buried in longer line", "You are in Elizabethan London.", true},
		{"partial word match", "Elizabethan", true},
		{"no match", "You are in a dark cave.", false},
		{"empty string", "", false},
		// MUD2 sends "Elizabethan" with capital-E; a lower-case variant must not match.
		{"wrong case", "elizabethan london", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := IsGameEntry(tt.text); got != tt.want {
				t.Errorf("IsGameEntry(%q) = %v, want %v", tt.text, got, tt.want)
			}
		})
	}
}

func TestIsClientModeSignal_PlainText(t *testing.T) {
	tests := []struct {
		name  string
		plain string
		want  bool
	}{
		{"option prompt", "Option: ", true},
		{"option buried", "Select Option: H for help", true},
		{"no match", "You hit the orc.", false},
		{"empty", "", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := IsClientModeSignal(nil, tt.plain); got != tt.want {
				t.Errorf("IsClientModeSignal(nil, %q) = %v, want %v", tt.plain, got, tt.want)
			}
		})
	}
}

func TestIsClientModeSignal_EscapeSequences(t *testing.T) {
	tests := []struct {
		name string
		raw  []byte
		want bool
	}{
		{"ESC-C", []byte{0x1b, '-', 'C'}, true},
		{"ESC-R", []byte{0x1b, '-', 'R'}, true},
		{"ESC-r", []byte{0x1b, '-', 'r'}, true},
		{"ESC-K", []byte{0x1b, '-', 'K'}, true},
		{"ESC-C buried", []byte{'x', 0x1b, '-', 'C', 'y'}, true},
		{"no escape", []byte("plain text"), false},
		{"partial escape no letter", []byte{0x1b, '-'}, false},
		{"wrong letter", []byte{0x1b, '-', 'X'}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := IsClientModeSignal(tt.raw, ""); got != tt.want {
				t.Errorf("IsClientModeSignal(%v, %q) = %v, want %v", tt.raw, "", got, tt.want)
			}
		})
	}
}
