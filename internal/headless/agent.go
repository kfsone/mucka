package headless

import (
	"encoding/json"
	"io"
	"strings"
	"sync"
	"time"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/mud2"
)

// AgentEmitter writes self-describing NDJSON events to an io.Writer.
// It implements core.TextSink and is safe for concurrent use.
type AgentEmitter struct {
	mu       sync.Mutex
	w        io.Writer
	colorMap *mud2.ColorMap
}

// NewAgentEmitter returns an AgentEmitter writing to w.
func NewAgentEmitter(w io.Writer) *AgentEmitter {
	return &AgentEmitter{w: w}
}

// SetColorMap sets the ANSI color→semantic-type map used to add a "semantic"
// field to text/span NDJSON events. Pass nil to disable the field.
// Safe to call concurrently with other methods.
func (a *AgentEmitter) SetColorMap(cm *mud2.ColorMap) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.colorMap = cm
}

// emit marshals fields as a JSON object with an injected RFC3339Nano timestamp
// and writes it followed by a newline. The mutex must NOT be held by the caller.
func (a *AgentEmitter) emit(fields map[string]any) {
	a.mu.Lock()
	defer a.mu.Unlock()
	fields["time"] = time.Now().Format(time.RFC3339Nano)
	data, err := json.Marshal(fields)
	if err != nil {
		return
	}
	a.w.Write(data)         //nolint:errcheck
	a.w.Write([]byte{'\n'}) //nolint:errcheck
}

// spansToText concatenates the plain text of all spans.
func spansToText(spans []ansi.Span) string {
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	return sb.String()
}

// AppendText strips ANSI escapes from text and emits an event:"text" line.
// A "semantic" field is added when the color map maps the text's color to a
// labeled type.
func (a *AgentEmitter) AppendText(text string) {
	spans := ansi.Parse(text)
	a.emitTextSpans(spans)
}

// AppendSpans emits an event:"text" line from pre-parsed spans.
// A "semantic" field is added when the color map maps the first span's color
// to a labeled type.
func (a *AgentEmitter) AppendSpans(spans []ansi.Span) {
	a.emitTextSpans(spans)
}

// emitTextSpans builds and emits an event:"text" JSON line for spans,
// adding a "semantic" field when the color map provides a tag.
func (a *AgentEmitter) emitTextSpans(spans []ansi.Span) {
	// Read colorMap under the lock to get a consistent snapshot.
	a.mu.Lock()
	cm := a.colorMap
	a.mu.Unlock()

	fields := map[string]any{
		"event": "text",
		"text":  spansToText(spans),
	}
	if tag := semanticTagLower(spans, cm); tag != "" {
		fields["semantic"] = tag
	}
	a.emit(fields)
}

// UpdatePartial emits an event:"partial" line with the current incomplete line.
func (a *AgentEmitter) UpdatePartial(spans []ansi.Span) {
	a.emit(map[string]any{
		"event": "partial",
		"text":  spansToText(spans),
	})
}

// EmitStats emits an event:"stats" line with all character statistics.
func (a *AgentEmitter) EmitStats(st *fes.Stats) {
	a.emit(map[string]any{
		"event":         "stats",
		"stamina":       st.Stamina,
		"max_stamina":   st.MaxStamina,
		"strength":      st.Strength,
		"max_strength":  st.MaxStrength,
		"dexterity":     st.Dexterity,
		"max_dexterity": st.MaxDexterity,
		"magic":         st.Magic,
		"max_magic":     st.MaxMagic,
		"score":         st.Score,
		"rank":          st.Rank,
		"level":         st.Level,
		"weather":       int(st.Weather),
		"dream_word":    st.DreamWord,
		"blind":         st.Blind,
		"deaf":          st.Deaf,
		"dumb":          st.Dumb,
		"crippled":      st.Crippled,
		"reset_minutes": st.ResetMinutes,
	})
}

// EmitDreamWord emits an event:"dreamword" line. Empty string means cleared.
func (a *AgentEmitter) EmitDreamWord(word string) {
	a.emit(map[string]any{
		"event": "dreamword",
		"word":  word,
	})
}

// EmitSent emits an event:"sent" line for a command sent to the MUD.
func (a *AgentEmitter) EmitSent(text string) {
	a.emit(map[string]any{
		"event": "sent",
		"text":  text,
	})
}

// EmitError emits an event:"error" line for a client-side error.
func (a *AgentEmitter) EmitError(text string) {
	a.emit(map[string]any{
		"event": "error",
		"text":  text,
	})
}
