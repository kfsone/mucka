package headless

import (
	"strings"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/mud2"
)

// emitter is satisfied by both AgentEmitter and PlainEmitter.
type emitter interface {
	core.TextSink
	EmitStats(*fes.Stats)
	EmitDreamWord(string)
	EmitSent(string)
	EmitError(string)
	SetColorMap(*mud2.ColorMap)
}

// spansSemanticTag returns the semantic tag for the first span in spans using
// the given color map. Returns "" if cm is nil, spans is empty, or the color
// does not map to a type with an assigned label.
func spansSemanticTag(spans []ansi.Span, cm *mud2.ColorMap) string {
	if cm == nil || len(spans) == 0 {
		return ""
	}
	typeNum := cm.Lookup(spans[0].FG, spans[0].BG)
	return mud2.SemanticTag(typeNum)
}

// semanticTagLower returns the semantic tag in lowercase form suitable for
// use as a JSON field value (e.g. "room-name"). Returns "" if no tag is found.
func semanticTagLower(spans []ansi.Span, cm *mud2.ColorMap) string {
	tag := spansSemanticTag(spans, cm)
	if tag == "" {
		return ""
	}
	return strings.ToLower(tag)
}
