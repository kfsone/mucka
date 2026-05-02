package ui

import (
	"testing"

	"github.com/kfsone/mucka/internal/ansi"
)

// makeSpanLine returns a one-span line with the given text.
func makeSpanLine(text string) []ansi.Span {
	return []ansi.Span{{Text: text}}
}

// ── TextPanel.drainPending / maxLines ─────────────────────────────────────

func TestTextPanel_DefaultMaxLines(t *testing.T) {
	p := NewTextPanel()
	if p.maxLines != defaultMaxLines {
		t.Errorf("default maxLines = %d, want %d", p.maxLines, defaultMaxLines)
	}
}

func TestTextPanel_SetMaxLines(t *testing.T) {
	p := NewTextPanel()
	p.SetMaxLines(100)
	if p.maxLines != 100 {
		t.Errorf("maxLines = %d after SetMaxLines(100), want 100", p.maxLines)
	}
}

func TestTextPanel_DrainPending_TrimsToMaxLines(t *testing.T) {
	p := NewTextPanel()
	p.SetMaxLines(3)

	// Enqueue 5 lines directly into pendingLines (bypassing goroutine safety
	// for test simplicity — drainPending is main-goroutine-only anyway).
	p.pendingMu.Lock()
	for i := 0; i < 5; i++ {
		p.pendingLines = append(p.pendingLines, makeSpanLine(string(rune('a'+i))))
	}
	p.pendingMu.Unlock()

	p.drainPending()

	if len(p.lines) != 3 {
		t.Fatalf("lines length = %d after trim, want 3", len(p.lines))
	}
	if p.lines[0][0].Text != "c" || p.lines[1][0].Text != "d" || p.lines[2][0].Text != "e" {
		t.Errorf("lines = %v, want [c d e]", p.lines)
	}
}

func TestTextPanel_DrainPending_ZeroMaxLinesUnlimited(t *testing.T) {
	p := NewTextPanel()
	p.SetMaxLines(0)

	p.pendingMu.Lock()
	for i := 0; i < 6000; i++ {
		p.pendingLines = append(p.pendingLines, makeSpanLine("x"))
	}
	p.pendingMu.Unlock()

	p.drainPending()

	if len(p.lines) != 6000 {
		t.Errorf("lines length = %d, want 6000 with unlimited maxLines", len(p.lines))
	}
}

func TestTextPanel_DrainPending_AccumulatesAcrossCalls(t *testing.T) {
	p := NewTextPanel()
	p.SetMaxLines(4)

	// First drain: 2 lines.
	p.pendingMu.Lock()
	p.pendingLines = append(p.pendingLines, makeSpanLine("1"), makeSpanLine("2"))
	p.pendingMu.Unlock()
	p.drainPending()

	// Second drain: 3 more lines → total 5, should trim to 4.
	p.pendingMu.Lock()
	p.pendingLines = append(p.pendingLines, makeSpanLine("3"), makeSpanLine("4"), makeSpanLine("5"))
	p.pendingMu.Unlock()
	p.drainPending()

	if len(p.lines) != 4 {
		t.Fatalf("lines length = %d, want 4", len(p.lines))
	}
	if p.lines[0][0].Text != "2" {
		t.Errorf("lines[0] = %q, want \"2\"", p.lines[0][0].Text)
	}
	if p.lines[3][0].Text != "5" {
		t.Errorf("lines[3] = %q, want \"5\"", p.lines[3][0].Text)
	}
}
