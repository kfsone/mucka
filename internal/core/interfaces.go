// Package core defines shared interfaces used across mucka packages.
package core

import "github.com/kfsone/mucka/internal/ansi"

// TextSink is a goroutine-safe sink for text output.
type TextSink interface {
	// AppendText parses s for ANSI SGR sequences and appends it as one line.
	AppendText(s string)
	// AppendSpans appends a pre-parsed line of styled spans.
	AppendSpans(spans []ansi.Span)
	// UpdatePartial replaces the currently-displayed partial (incomplete) line.
	// The partial is promoted to a permanent line when the next AppendSpans fires.
	UpdatePartial(spans []ansi.Span)
}

// Invalidator triggers a UI redraw.
type Invalidator interface {
	Invalidate()
}
