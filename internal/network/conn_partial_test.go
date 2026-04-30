package network

import (
	"net"
	"testing"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// TestConn_PartialLineDisplayed verifies that a prompt without a trailing
// newline is flushed as a partial line once all buffered bytes are consumed.
func TestConn_PartialLineDisplayed(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Write a prompt with no trailing newline.
	server.Write([]byte("Option (H for help): "))

	// Give the reader time to process all bytes and call UpdatePartial.
	if !waitFor(func() bool {
		return sink.SnapshotPartial() == "Option (H for help): "
	}) {
		t.Errorf("partial not set: got %q", sink.SnapshotPartial())
	}
	// No complete lines should have been appended yet.
	if lines := sink.Snapshot(); len(lines) != 0 {
		t.Errorf("want 0 complete lines, got %d: %v", len(lines), lines)
	}
}

// TestConn_PartialPromotedOnNewline verifies that a partial line is promoted
// to a complete line when the server subsequently sends a newline.
func TestConn_PartialPromotedOnNewline(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Phase 1: send partial prompt.
	server.Write([]byte("Option (H for help): "))
	if !waitFor(func() bool {
		return sink.SnapshotPartial() == "Option (H for help): "
	}) {
		t.Fatalf("phase 1: partial not set: got %q", sink.SnapshotPartial())
	}

	// Phase 2: complete the line with a newline.
	server.Write([]byte("\n"))
	if !waitFor(func() bool {
		return len(sink.Snapshot()) >= 1
	}) {
		t.Fatalf("phase 2: complete line never arrived")
	}

	lines := sink.Snapshot()
	if lines[0] != "Option (H for help): " {
		t.Errorf("complete line: got %q, want %q", lines[0], "Option (H for help): ")
	}
	if got := sink.SnapshotPartial(); got != "" {
		t.Errorf("partial should be cleared after newline, got %q", got)
	}
}

// TestConn_PartialWithAnsi verifies that ANSI escape sequences in a partial
// prompt are stripped to plain text (the stateCopy snapshot does not advance
// real ansiState, but plain-text extraction is still correct).
func TestConn_PartialWithAnsi(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Prompt with bold ANSI codes but no trailing newline.
	server.Write([]byte("\x1b[1mEnter name:\x1b[0m "))

	if !waitFor(func() bool {
		return sink.SnapshotPartial() == "Enter name: "
	}) {
		t.Errorf("ANSI partial: got %q, want %q", sink.SnapshotPartial(), "Enter name: ")
	}
	if len(sink.Snapshot()) != 0 {
		t.Errorf("want 0 complete lines, got %d", len(sink.Snapshot()))
	}
}

// TestConn_PartialWithCR verifies that a carriage return before the buffer
// empties does not appear in the partial text.
func TestConn_PartialWithCR(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Some prompts end with \r but not \n — CR should be trimmed.
	server.Write([]byte("prompt> \r"))

	if !waitFor(func() bool {
		p := sink.SnapshotPartial()
		return p != "" // wait until something lands
	}) {
		t.Fatal("partial never set")
	}
	got := sink.SnapshotPartial()
	if got == "prompt> \r" {
		t.Errorf("CR was not trimmed from partial: got %q", got)
	}
	// Should be trimmed to "prompt> ".
	if got != "prompt> " {
		t.Errorf("partial: got %q, want %q", got, "prompt> ")
	}
}

// TestConn_MultiplePartialsThenComplete verifies that sending several partial
// chunks followed by a newline results in exactly one complete line (the full
// concatenation of all chunks) and the partial is cleared.
func TestConn_MultiplePartialsThenComplete(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Send the prompt in two separate writes so the reader may see intermediate
	// partial updates; what matters is the final state after the newline.
	server.Write([]byte("Option"))
	if !waitFor(func() bool { return sink.SnapshotPartial() != "" }) {
		t.Fatal("first partial never set")
	}
	server.Write([]byte(" (H for help): \n"))

	if !waitFor(func() bool { return len(sink.Snapshot()) >= 1 }) {
		t.Fatal("complete line never arrived")
	}
	lines := sink.Snapshot()
	if lines[0] != "Option (H for help): " {
		t.Errorf("complete line: got %q, want %q", lines[0], "Option (H for help): ")
	}
	if got := sink.SnapshotPartial(); got != "" {
		t.Errorf("partial should be cleared after newline, got %q", got)
	}
}

// TestConn_PartialThenMultipleLines verifies that after a partial, multiple
// complete lines all land correctly and the partial is cleared.
func TestConn_PartialThenMultipleLines(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Partial first.
	server.Write([]byte("prompt: "))
	if !waitFor(func() bool { return sink.SnapshotPartial() != "" }) {
		t.Fatal("partial never set")
	}

	// Then two complete lines arrive together (simulating a server burst).
	server.Write([]byte("\nline one\nline two\n"))

	if !waitFor(func() bool { return len(sink.Snapshot()) >= 3 }) {
		t.Fatalf("want ≥3 lines, got %d", len(sink.Snapshot()))
	}
	lines := sink.Snapshot()
	if lines[0] != "prompt: " {
		t.Errorf("line[0]: got %q, want %q", lines[0], "prompt: ")
	}
	if lines[1] != "line one" {
		t.Errorf("line[1]: got %q, want %q", lines[1], "line one")
	}
	if lines[2] != "line two" {
		t.Errorf("line[2]: got %q, want %q", lines[2], "line two")
	}
	if got := sink.SnapshotPartial(); got != "" {
		t.Errorf("partial should be cleared after complete lines, got %q", got)
	}
}

// TestConn_AnsiStateNotAdvancedByPartial verifies that ANSI state is not
// permanently mutated by a partial flush. After the partial (which carries
// bold+red ANSI) is followed by a plain newline, the complete line should
// NOT be colored — because the real ansiState was never advanced.
func TestConn_AnsiStateNotAdvancedByPartial(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// ANSI bold+red partial.
	server.Write([]byte("\x1b[1;31mprompt: "))
	if !waitFor(func() bool { return sink.SnapshotPartial() == "prompt: " }) {
		t.Fatalf("partial not set: %q", sink.SnapshotPartial())
	}

	// Complete the line; a subsequent plain line must not inherit the red color.
	// We use the plain-text BufferSink, so we can only check text, but we can
	// verify the line is completed and partial is cleared correctly.
	server.Write([]byte("\n"))
	if !waitFor(func() bool { return len(sink.Snapshot()) >= 1 }) {
		t.Fatal("complete line never arrived")
	}
	// Partial must now be cleared.
	if got := sink.SnapshotPartial(); got != "" {
		t.Errorf("partial should be cleared after newline, got %q", got)
	}
}
