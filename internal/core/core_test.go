package core_test

import (
	"sync"
	"testing"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/core"
)

// Compile-time interface checks.
var _ core.TextSink = (*core.BufferSink)(nil)
var _ core.TextSink = (*core.StdioSink)(nil)
var _ core.Invalidator = (*core.NopInvalidator)(nil)

// ---------------------------------------------------------------------------
// NopInvalidator
// ---------------------------------------------------------------------------

func TestNopInvalidatorDoesNotPanic(t *testing.T) {
	n := &core.NopInvalidator{}
	n.Invalidate() // must not panic
}

// ---------------------------------------------------------------------------
// BufferSink – AppendText
// ---------------------------------------------------------------------------

func TestBufferSinkAppendText_PlainText(t *testing.T) {
	var b core.BufferSink
	b.AppendText("hello world")
	if len(b.Lines) != 1 {
		t.Fatalf("want 1 line, got %d", len(b.Lines))
	}
	if b.Lines[0] != "hello world" {
		t.Errorf("want %q, got %q", "hello world", b.Lines[0])
	}
}

func TestBufferSinkAppendText_AnsiStripped(t *testing.T) {
	var b core.BufferSink
	b.AppendText("\x1b[31mred text\x1b[0m")
	if len(b.Lines) != 1 {
		t.Fatalf("want 1 line, got %d", len(b.Lines))
	}
	if b.Lines[0] != "red text" {
		t.Errorf("ANSI not stripped: got %q", b.Lines[0])
	}
}

func TestBufferSinkAppendText_MultipleLines(t *testing.T) {
	var b core.BufferSink
	b.AppendText("line one")
	b.AppendText("line two")
	b.AppendText("line three")
	if len(b.Lines) != 3 {
		t.Fatalf("want 3 lines, got %d", len(b.Lines))
	}
}

// ---------------------------------------------------------------------------
// BufferSink – AppendSpans
// ---------------------------------------------------------------------------

func TestBufferSinkAppendSpans_TextExtraction(t *testing.T) {
	var b core.BufferSink
	spans := []ansi.Span{
		{Text: "Hello"},
		{Text: ", "},
		{Text: "World"},
	}
	b.AppendSpans(spans)
	if len(b.Lines) != 1 {
		t.Fatalf("want 1 line, got %d", len(b.Lines))
	}
	if b.Lines[0] != "Hello, World" {
		t.Errorf("want %q, got %q", "Hello, World", b.Lines[0])
	}
}

func TestBufferSinkAppendSpans_EmptySpans(t *testing.T) {
	var b core.BufferSink
	b.AppendSpans(nil)
	if len(b.Lines) != 1 {
		t.Fatalf("want 1 line for empty spans, got %d", len(b.Lines))
	}
	if b.Lines[0] != "" {
		t.Errorf("want empty string, got %q", b.Lines[0])
	}
}

// ---------------------------------------------------------------------------
// BufferSink – Reset
// ---------------------------------------------------------------------------

func TestBufferSinkReset_ClearsLines(t *testing.T) {
	var b core.BufferSink
	b.AppendText("a")
	b.AppendText("b")
	if len(b.Lines) != 2 {
		t.Fatalf("precondition: want 2 lines before reset, got %d", len(b.Lines))
	}
	b.Reset()
	if len(b.Lines) != 0 {
		t.Errorf("after Reset: want 0 lines, got %d", len(b.Lines))
	}
}

func TestBufferSinkReset_CanAppendAfterReset(t *testing.T) {
	var b core.BufferSink
	b.AppendText("before reset")
	b.Reset()
	b.AppendText("after reset")
	if len(b.Lines) != 1 || b.Lines[0] != "after reset" {
		t.Errorf("after Reset+AppendText: got %v", b.Lines)
	}
}

// ---------------------------------------------------------------------------
// BufferSink – goroutine safety
// ---------------------------------------------------------------------------

func TestBufferSinkConcurrentAppendText(t *testing.T) {
	const goroutines = 50
	const linesEach = 100
	var b core.BufferSink
	var wg sync.WaitGroup
	wg.Add(goroutines)
	for i := 0; i < goroutines; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < linesEach; j++ {
				b.AppendText("concurrent line")
			}
		}()
	}
	wg.Wait()
	if len(b.Lines) != goroutines*linesEach {
		t.Errorf("want %d lines, got %d", goroutines*linesEach, len(b.Lines))
	}
}

func TestBufferSinkConcurrentAppendSpans(t *testing.T) {
	const goroutines = 50
	const linesEach = 100
	spans := []ansi.Span{{Text: "span"}}
	var b core.BufferSink
	var wg sync.WaitGroup
	wg.Add(goroutines)
	for i := 0; i < goroutines; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < linesEach; j++ {
				b.AppendSpans(spans)
			}
		}()
	}
	wg.Wait()
	if len(b.Lines) != goroutines*linesEach {
		t.Errorf("want %d lines, got %d", goroutines*linesEach, len(b.Lines))
	}
}

func TestBufferSinkConcurrentResetAndAppend(t *testing.T) {
	// Verify no data races when Reset and AppendText run concurrently.
	var b core.BufferSink
	var wg sync.WaitGroup
	const workers = 20
	wg.Add(workers * 2)
	for i := 0; i < workers; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				b.AppendText("writer")
			}
		}()
		go func() {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				b.Reset()
			}
		}()
	}
	wg.Wait()
	// No assertion on line count; the goal is absence of data races.
}

// ---------------------------------------------------------------------------
// StdioSink – compile-time and smoke checks
// ---------------------------------------------------------------------------

func TestStdioSinkImplementsTextSink(t *testing.T) {
	// Compile-time check is at the top of this file; this test exists for
	// documentation and so 'go test -v' reports it explicitly.
	var _ core.TextSink = (*core.StdioSink)(nil)
}

// ---------------------------------------------------------------------------
// BufferSink – UpdatePartial
// ---------------------------------------------------------------------------

func TestBufferSink_UpdatePartial(t *testing.T) {
	var b core.BufferSink
	spans := []ansi.Span{{Text: "Option (H for help): "}}
	b.UpdatePartial(spans)
	if got := b.SnapshotPartial(); got != "Option (H for help): " {
		t.Errorf("UpdatePartial: got %q, want %q", got, "Option (H for help): ")
	}
	// Lines should be unaffected.
	if len(b.Lines) != 0 {
		t.Errorf("UpdatePartial: want 0 lines, got %d", len(b.Lines))
	}
}

func TestBufferSink_UpdatePartial_ClearedByAppend(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{{Text: "partial text"}})
	if b.SnapshotPartial() == "" {
		t.Fatal("precondition: Partial should be set before AppendSpans")
	}
	b.AppendSpans([]ansi.Span{{Text: "full line"}})
	if got := b.SnapshotPartial(); got != "" {
		t.Errorf("AppendSpans should clear Partial, got %q", got)
	}
	if len(b.Lines) != 1 || b.Lines[0] != "full line" {
		t.Errorf("AppendSpans: got lines %v", b.Lines)
	}
}

func TestBufferSink_UpdatePartial_MultipleSpans(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{
		{Text: "Enter "},
		{Text: "your "},
		{Text: "name: "},
	})
	if got := b.SnapshotPartial(); got != "Enter your name: " {
		t.Errorf("got %q, want %q", got, "Enter your name: ")
	}
}

func TestBufferSink_UpdatePartial_NilSpans(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{{Text: "something"}})
	b.UpdatePartial(nil)
	if got := b.SnapshotPartial(); got != "" {
		t.Errorf("nil spans should produce empty Partial, got %q", got)
	}
}

func TestBufferSink_UpdatePartial_EmptySpans(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{{Text: "something"}})
	b.UpdatePartial([]ansi.Span{})
	if got := b.SnapshotPartial(); got != "" {
		t.Errorf("empty spans should produce empty Partial, got %q", got)
	}
}

func TestBufferSink_UpdatePartial_LastWins(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{{Text: "first partial"}})
	b.UpdatePartial([]ansi.Span{{Text: "second partial"}})
	b.UpdatePartial([]ansi.Span{{Text: "final prompt: "}})
	if got := b.SnapshotPartial(); got != "final prompt: " {
		t.Errorf("last UpdatePartial should win; got %q", got)
	}
	if len(b.Lines) != 0 {
		t.Errorf("want 0 lines, got %d", len(b.Lines))
	}
}

func TestBufferSink_SnapshotPartial_ZeroValue(t *testing.T) {
	var b core.BufferSink
	if got := b.SnapshotPartial(); got != "" {
		t.Errorf("zero-value BufferSink: Partial should be empty, got %q", got)
	}
}

// TestBufferSink_AppendText_DoesNotClearPartial documents that AppendText does
// not clear the Partial field. Only AppendSpans does (matching the network
// reader which uses AppendSpans exclusively for MUD output).
func TestBufferSink_AppendText_DoesNotClearPartial(t *testing.T) {
	var b core.BufferSink
	b.UpdatePartial([]ansi.Span{{Text: "prompt: "}})
	b.AppendText("status message")
	if got := b.SnapshotPartial(); got != "prompt: " {
		t.Errorf("AppendText should not clear Partial; got %q", got)
	}
	if len(b.Lines) != 1 {
		t.Errorf("want 1 line from AppendText, got %d", len(b.Lines))
	}
}

func TestBufferSink_UpdatePartial_Concurrent(t *testing.T) {
	// Verify no data races when UpdatePartial and SnapshotPartial run concurrently.
	var b core.BufferSink
	var wg sync.WaitGroup
	const workers = 20
	wg.Add(workers * 2)
	for i := 0; i < workers; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				b.UpdatePartial([]ansi.Span{{Text: "partial"}})
			}
		}()
		go func() {
			defer wg.Done()
			for j := 0; j < 50; j++ {
				_ = b.SnapshotPartial()
			}
		}()
	}
	wg.Wait()
}
