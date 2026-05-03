package mud2

import "testing"

func TestSemanticTag_KnownTypes(t *testing.T) {
	tests := []struct {
		typeNum int
		want    string
	}{
		{0, ""},
		{1, "PROMPT"},
		{3, "ROOM-NAME"},
		{4, "ROOM-DESC"},
		{5, "FEATURES"},
		{6, "OBJECT"},
		{7, "TRINKET"},
		{8, "TREASURE"},
		{9, "CREATURE"},
		{10, "CREATURE"},
		{11, "PLAYER"},
		{12, "WIZ"},
		{13, "SAY"},
		{14, "EMOTE"},
		{15, "TOLD"},
		{16, "ACT"},
		{17, "SHOUT"},
		{18, "SAY"},
		{19, "FIGHT"},
		{20, "FIGHT"},
		{21, "FIGHT"},
		{22, "FIGHT"},
		{24, "SPELL"},
		{29, "NOISE"},
		{30, "INFO"},
		{31, "WEATHER"},
		{32, "WEATHER"},
		{33, "WEATHER"},
	}
	for _, tt := range tests {
		if got := SemanticTag(tt.typeNum); got != tt.want {
			t.Errorf("SemanticTag(%d) = %q, want %q", tt.typeNum, got, tt.want)
		}
	}
}

func TestSemanticTag_DefaultAndUnknown(t *testing.T) {
	// Type 0 is default text — no tag.
	if got := SemanticTag(0); got != "" {
		t.Errorf("SemanticTag(0) = %q, want empty", got)
	}
	// Negative types are out of range.
	if got := SemanticTag(-1); got != "" {
		t.Errorf("SemanticTag(-1) = %q, want empty", got)
	}
	// Type 61 is out of range.
	if got := SemanticTag(61); got != "" {
		t.Errorf("SemanticTag(61) = %q, want empty", got)
	}
}

func TestSemanticTag_ReservedTypesAreEmpty(t *testing.T) {
	// Types 2, 23, 25-28 are reserved and should return "".
	reserved := []int{2, 23, 25, 26, 27, 28}
	for _, n := range reserved {
		if got := SemanticTag(n); got != "" {
			t.Errorf("SemanticTag(%d) = %q, want empty for reserved type", n, got)
		}
	}
}
