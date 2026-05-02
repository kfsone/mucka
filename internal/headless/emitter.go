package headless

import (
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/fes"
)

// emitter is satisfied by both AgentEmitter and PlainEmitter.
type emitter interface {
	core.TextSink
	EmitStats(*fes.Stats)
	EmitDreamWord(string)
	EmitSent(string)
	EmitError(string)
}
