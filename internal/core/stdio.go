package core

import (
	"fmt"
	"strings"

	"github.com/kfsone/mucka/internal/ansi"
)

// StdioSink is a TextSink that writes plain text to stdout via fmt.Println.
// ANSI escape sequences are stripped before printing.
type StdioSink struct{}

// AppendText strips ANSI escapes from s and prints the plain text.
func (s *StdioSink) AppendText(text string) {
	spans := ansi.Parse(text)
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	fmt.Println(sb.String())
}

// AppendSpans concatenates the text of each span and prints the result.
func (s *StdioSink) AppendSpans(spans []ansi.Span) {
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	fmt.Println(sb.String())
}

// UpdatePartial overwrites the current terminal line with the plain text of
// spans. Uses a carriage return so the next full line can overwrite it.
func (s *StdioSink) UpdatePartial(spans []ansi.Span) {
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	fmt.Print("\r" + sb.String())
}
