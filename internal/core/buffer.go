package core

import (
	"strings"
	"sync"

	"github.com/kfsone/mucka/internal/ansi"
)

// BufferSink is a goroutine-safe TextSink that accumulates output in Lines.
// Intended for use in tests and headless contexts.
type BufferSink struct {
	mu      sync.Mutex
	Lines   []string
	Partial string // current partial (incomplete) line; exported for tests
}

// AppendText records s (with ANSI stripped to plain text) as a new line.
func (b *BufferSink) AppendText(s string) {
	spans := ansi.Parse(s)
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	b.mu.Lock()
	b.Lines = append(b.Lines, sb.String())
	b.mu.Unlock()
}

// AppendSpans concatenates the text of each span and records it as a new line.
// It also clears any in-progress partial (the partial has been finalised).
func (b *BufferSink) AppendSpans(spans []ansi.Span) {
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	b.mu.Lock()
	b.Lines = append(b.Lines, sb.String())
	b.Partial = ""
	b.mu.Unlock()
}

// UpdatePartial replaces the current partial (incomplete) line with the
// plain-text content of spans. Goroutine-safe.
func (b *BufferSink) UpdatePartial(spans []ansi.Span) {
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	b.mu.Lock()
	b.Partial = sb.String()
	b.mu.Unlock()
}

// SnapshotPartial returns the current partial line. Goroutine-safe.
func (b *BufferSink) SnapshotPartial() string {
	b.mu.Lock()
	p := b.Partial
	b.mu.Unlock()
	return p
}

// Reset clears all accumulated lines. Goroutine-safe.
func (b *BufferSink) Reset() {
	b.mu.Lock()
	b.Lines = nil
	b.mu.Unlock()
}

// Snapshot returns a copy of the current Lines slice. Goroutine-safe.
func (b *BufferSink) Snapshot() []string {
	b.mu.Lock()
	out := append([]string(nil), b.Lines...)
	b.mu.Unlock()
	return out
}
