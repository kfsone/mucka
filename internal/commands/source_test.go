package commands

import (
	"testing"

	"github.com/kfsone/mucka/internal/ui"
)

func TestSourceTokensPlain(t *testing.T) {
	ops := sourceTokens("hello world")
	want := []sourceOp{
		{ui.OpText, "hello world"},
	}
	if len(ops) != len(want) {
		t.Fatalf("expected %d ops, got %d: %v", len(want), len(ops), ops)
	}
	for i, op := range ops {
		if op != want[i] {
			t.Errorf("op[%d]: got %v, want %v", i, op, want[i])
		}
	}
}

func TestSourceTokensSpecial(t *testing.T) {
	tests := []struct {
		input string
		want  sourceOp
	}{
		{"{enter}", sourceOp{ui.OpSubmit, ""}},
		{"{bs}", sourceOp{ui.OpBS, ""}},
		{"{clear}", sourceOp{ui.OpClear, ""}},
	}
	for _, tt := range tests {
		ops := sourceTokens(tt.input)
		if len(ops) != 1 {
			t.Errorf("sourceTokens(%q): expected 1 op, got %d", tt.input, len(ops))
			continue
		}
		if ops[0] != tt.want {
			t.Errorf("sourceTokens(%q): got %v, want %v", tt.input, ops[0], tt.want)
		}
	}
}

func TestSourceTokensMixed(t *testing.T) {
	ops := sourceTokens("go north {enter} look")
	want := []sourceOp{
		{ui.OpText, "go north "},
		{ui.OpSubmit, ""},
		{ui.OpText, " look"},
	}
	if len(ops) != len(want) {
		t.Fatalf("expected %d ops, got %d: %v", len(want), len(ops), ops)
	}
	for i, op := range ops {
		if op != want[i] {
			t.Errorf("op[%d]: got %v, want %v", i, op, want[i])
		}
	}
}

func TestSourceTokensEmpty(t *testing.T) {
	ops := sourceTokens("")
	if len(ops) != 0 {
		t.Errorf("expected no ops for empty line, got %v", ops)
	}
}

func TestSourceTokensOnlySpecials(t *testing.T) {
	ops := sourceTokens("{bs} {clear} {enter}")
	want := []sourceOp{
		{ui.OpBS, ""},
		{ui.OpText, " "},
		{ui.OpClear, ""},
		{ui.OpText, " "},
		{ui.OpSubmit, ""},
	}
	if len(ops) != len(want) {
		t.Fatalf("expected %d ops, got %d: %v", len(want), len(ops), ops)
	}
	for i, op := range ops {
		if op != want[i] {
			t.Errorf("op[%d]: got %v, want %v", i, op, want[i])
		}
	}
}

// TestSourceTokensUnknownToken verifies that unrecognised {token} strings pass through as OpText.
func TestSourceTokensUnknownToken(t *testing.T) {
	ops := sourceTokens("{blah}")
	if len(ops) != 1 {
		t.Fatalf("expected 1 op, got %d: %v", len(ops), ops)
	}
	if ops[0].kind != ui.OpText {
		t.Errorf("expected kind %q, got %q", ui.OpText, ops[0].kind)
	}
	if ops[0].value != "{blah}" {
		t.Errorf("expected value %q, got %q", "{blah}", ops[0].value)
	}
}

// TestSourceTokensUnclosedMarker verifies that an unclosed '{' is preserved as OpText.
func TestSourceTokensUnclosedMarker(t *testing.T) {
	ops := sourceTokens("hello {world")
	want := []sourceOp{
		{ui.OpText, "hello "},
		{ui.OpText, "{world"},
	}
	if len(ops) != len(want) {
		t.Fatalf("expected %d ops, got %d: %v", len(want), len(ops), ops)
	}
	for i, op := range ops {
		if op != want[i] {
			t.Errorf("op[%d]: got %v, want %v", i, op, want[i])
		}
	}
}
