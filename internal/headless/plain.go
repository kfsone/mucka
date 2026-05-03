package headless

import (
	"fmt"
	"io"
	"sync"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/mud2"
)

// PlainEmitter writes plain-text, LLM-friendly output to an io.Writer.
// All writes are mutex-protected for safe concurrent use.
type PlainEmitter struct {
	mu         sync.Mutex
	w          io.Writer
	lastPrompt string
	colorMap   *mud2.ColorMap
}

// NewPlainEmitter returns a PlainEmitter writing to w.
func NewPlainEmitter(w io.Writer) *PlainEmitter {
	return &PlainEmitter{w: w}
}

// SetColorMap sets the ANSI color→semantic-type map used to prefix output
// lines with their semantic tag (e.g. "[ROOM-NAME]"). Pass nil to disable
// tagging. Safe to call concurrently with other methods.
func (p *PlainEmitter) SetColorMap(cm *mud2.ColorMap) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.colorMap = cm
}

// appendSpansLocked writes spans as a (possibly tagged) line to p.w.
// The caller must hold p.mu.
func (p *PlainEmitter) appendSpansLocked(spans []ansi.Span) {
	text := spansToText(spans)
	if tag := spansSemanticTag(spans, p.colorMap); tag != "" {
		fmt.Fprintf(p.w, "[%s] %s\n", tag, text)
	} else {
		fmt.Fprintln(p.w, text)
	}
}

// AppendText strips ANSI escapes from text and prints the plain line,
// prefixed with a semantic tag if the color map maps the text's color.
func (p *PlainEmitter) AppendText(text string) {
	spans := ansi.Parse(text)
	p.mu.Lock()
	defer p.mu.Unlock()
	p.appendSpansLocked(spans)
}

// AppendSpans concatenates span text and prints the plain line, prefixed
// with a semantic tag if the color map maps the first span's color.
func (p *PlainEmitter) AppendSpans(spans []ansi.Span) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.appendSpansLocked(spans)
}

// UpdatePartial prints "[PROMPT] <text>" followed by a blank line when the
// text differs from the last emitted prompt (deduplicates identical prompts).
func (p *PlainEmitter) UpdatePartial(spans []ansi.Span) {
	text := spansToText(spans)
	p.mu.Lock()
	defer p.mu.Unlock()
	if text == p.lastPrompt {
		return
	}
	p.lastPrompt = text
	fmt.Fprintf(p.w, "[PROMPT] %s\n\n", text)
}

// EmitStats prints a single-line status summary with all character statistics.
func (p *PlainEmitter) EmitStats(st *fes.Stats) {
	p.mu.Lock()
	defer p.mu.Unlock()
	fmt.Fprintf(p.w, "[STATUS] sta=%d/%d str=%d/%d dex=%d/%d mag=%d/%d score=%s rank=%s level=%d weather=%d\n",
		st.Stamina, st.MaxStamina,
		st.Strength, st.MaxStrength,
		st.Dexterity, st.MaxDexterity,
		st.Magic, st.MaxMagic,
		fes.FormatInt(st.Score),
		st.Rank,
		st.Level,
		int(st.Weather),
	)
}

// EmitDreamWord prints the current dream word, or "(none)" if cleared.
func (p *PlainEmitter) EmitDreamWord(word string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if word != "" {
		fmt.Fprintf(p.w, "[DREAMWORD] %s\n", word)
	} else {
		fmt.Fprintln(p.w, "[DREAMWORD] (none)")
	}
}

// EmitSent prints a record of a command sent to the MUD.
func (p *PlainEmitter) EmitSent(text string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	fmt.Fprintf(p.w, "[SENT] %s\n", text)
}

// EmitError prints a client-side error message.
func (p *PlainEmitter) EmitError(text string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	fmt.Fprintf(p.w, "[ERROR] %s\n", text)
}
