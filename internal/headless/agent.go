package headless

import (
	"encoding/json"
	"io"
	"strings"
	"sync"
	"time"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/fes"
)

// AgentEmitter writes self-describing NDJSON events to an io.Writer.
// It implements core.TextSink and is safe for concurrent use.
type AgentEmitter struct {
	mu sync.Mutex
	w  io.Writer
}

// NewAgentEmitter returns an AgentEmitter writing to w.
func NewAgentEmitter(w io.Writer) *AgentEmitter {
	return &AgentEmitter{w: w}
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
func (a *AgentEmitter) AppendText(text string) {
	spans := ansi.Parse(text)
	a.emit(map[string]any{
		"event": "text",
		"text":  spansToText(spans),
	})
}

// AppendSpans emits an event:"text" line from pre-parsed spans.
func (a *AgentEmitter) AppendSpans(spans []ansi.Span) {
	a.emit(map[string]any{
		"event": "text",
		"text":  spansToText(spans),
	})
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
