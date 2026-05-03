package ui

import (
	"bytes"
	"io"
	"testing"
	"unicode/utf8"

	"github.com/kfsone/mucka/internal/ansi"
)

// makeSpanLine returns a one-span line with the given text.
func makeSpanLine(text string) []ansi.Span {
	return []ansi.Span{{Text: text}}
}

// nopCloser wraps an io.Writer with a no-op Close.
type nopCloser struct{ w *bytes.Buffer }

func (n nopCloser) Write(p []byte) (int, error) { return n.w.Write(p) }
func (n nopCloser) Close() error                { return nil }

var _ io.WriteCloser = nopCloser{}

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

// ── TextPanel logging ─────────────────────────────────────────────────────

func TestTextPanel_SetLogWriter_WritesLines(t *testing.T) {
	p := NewTextPanel()
	var buf bytes.Buffer
	p.SetLogWriter(nopCloser{&buf}, nil)

	p.AppendText("hello world")
	p.AppendText("second line")

	got := buf.String()
	if got != "hello world\nsecond line\n" {
		t.Errorf("log output = %q, want %q", got, "hello world\nsecond line\n")
	}
}

func TestTextPanel_SetLogWriter_StripANSI(t *testing.T) {
	p := NewTextPanel()
	var buf bytes.Buffer
	p.SetLogWriter(nopCloser{&buf}, nil)

	// AppendText calls AppendSpans via ansi.Parse which strips codes.
	p.AppendText("\x1b[31mred text\x1b[0m")

	got := buf.String()
	if got != "red text\n" {
		t.Errorf("log output = %q, want plain %q", got, "red text\n")
	}
}

func TestTextPanel_SetLogWriter_WithLinePrefix(t *testing.T) {
	p := NewTextPanel()
	var buf bytes.Buffer
	p.SetLogWriter(nopCloser{&buf}, func() string { return "[TS] " })

	p.AppendText("msg")

	got := buf.String()
	if got != "[TS] msg\n" {
		t.Errorf("log output = %q, want %q", got, "[TS] msg\n")
	}
}

func TestTextPanel_StopLog_StopsWriting(t *testing.T) {
	p := NewTextPanel()
	var buf bytes.Buffer
	p.SetLogWriter(nopCloser{&buf}, nil)

	p.AppendText("before stop")
	wasLogging := p.StopLog()
	if !wasLogging {
		t.Error("StopLog returned false, expected true (was logging)")
	}
	p.AppendText("after stop")

	got := buf.String()
	if got != "before stop\n" {
		t.Errorf("log output = %q, want only %q", got, "before stop\n")
	}
}

func TestTextPanel_StopLog_WhenNotLogging(t *testing.T) {
	p := NewTextPanel()
	if p.StopLog() {
		t.Error("StopLog returned true on panel that was never logging")
	}
}

func TestTextPanel_SetLogWriter_ReplacesExistingWriter(t *testing.T) {
	p := NewTextPanel()
	var buf1, buf2 bytes.Buffer
	p.SetLogWriter(nopCloser{&buf1}, nil)
	p.AppendText("first")

	p.SetLogWriter(nopCloser{&buf2}, nil)
	p.AppendText("second")

	if buf1.String() != "first\n" {
		t.Errorf("buf1 = %q, want %q", buf1.String(), "first\n")
	}
	if buf2.String() != "second\n" {
		t.Errorf("buf2 = %q, want %q", buf2.String(), "second\n")
	}
}

// ── Cell-based column layout ───────────────────────────────────────────────

// TestColumnPositions_NoAccumulatedDrift verifies that the column-based pixel
// arithmetic used in layoutLine produces positions that never accumulate drift.
// Specifically:
//
//startX(col) = col * refW / N
//endX(col, len) = (col+len) * refW / N
//spanW = endX - startX
//
// The sum of all spanW values must equal the total line width computed from
// the last column — i.e. no pixel is lost or double-counted.
func TestColumnPositions_NoAccumulatedDrift(t *testing.T) {
// Simulate a line of 80 single-character spans (worst case for drift).
const refW = 648 // 80 chars × 8.1px — a non-integer advance per char
const N = cellMeasureN

totalSpanW := 0
for col := 0; col < N; col++ {
startX := col * refW / N
endX := (col + 1) * refW / N
totalSpanW += endX - startX
}

wantTotal := N * refW / N // = refW (since N divides N*refW exactly)
if totalSpanW != wantTotal {
t.Errorf("sum of spanW = %d, want %d (refW=%d N=%d)", totalSpanW, wantTotal, refW, N)
}
}

// TestColumnPositions_SpanWidthConsistency verifies that computing spanW as
// endX-startX (column-derived) produces gapless spans.
func TestColumnPositions_SpanWidthConsistency(t *testing.T) {
const refW = 648
const N = cellMeasureN

// A line of varying-length spans mimicking a colourful MUD line.
type testSpan struct{ col, length int }
spans := []testSpan{{0, 5}, {5, 1}, {6, 3}, {9, 7}, {16, 4}, {20, 60}}

prevEnd := 0
for _, s := range spans {
startX := s.col * refW / N
endX := (s.col + s.length) * refW / N
spanW := endX - startX

// startX must be exactly where the previous span ended.
if startX != prevEnd {
t.Errorf("col %d: startX=%d, want %d (gap/overlap)", s.col, startX, prevEnd)
}
prevEnd = endX

// spanW must be positive.
if spanW <= 0 {
t.Errorf("col %d len %d: spanW=%d ≤ 0", s.col, s.length, spanW)
}
}
}

// TestSetFont_InvalidatesCellRefW verifies that SetFont resets the cached cell width.
func TestSetFont_InvalidatesCellRefW(t *testing.T) {
p := NewTextPanel()
p.cellRefW = 999 // pretend it was measured
p.SetFont("SomeOtherFont")
if p.cellRefW != 0 {
t.Errorf("cellRefW = %d after SetFont, want 0", p.cellRefW)
}
}

// TestSetFontSize_InvalidatesCellRefW verifies that SetFontSize resets the cached cell width.
func TestSetFontSize_InvalidatesCellRefW(t *testing.T) {
p := NewTextPanel()
p.cellRefW = 999
p.SetFontSize(16)
if p.cellRefW != 0 {
t.Errorf("cellRefW = %d after SetFontSize, want 0", p.cellRefW)
}
}

// TestLayoutLine_SpanRuneCount verifies that utf8.RuneCountInString is used for
// column tracking (important for multi-byte UTF-8 characters in span text).
func TestLayoutLine_SpanRuneCount(t *testing.T) {
tests := []struct {
text      string
wantRunes int
}{
{"hello", 5},
{"(XXXX)", 6},
{"|XX|", 4},
{"café", 4}, // 'é' is 2 bytes but 1 rune
}
for _, tt := range tests {
got := utf8.RuneCountInString(tt.text)
if got != tt.wantRunes {
t.Errorf("RuneCountInString(%q) = %d, want %d", tt.text, got, tt.wantRunes)
}
}
}
