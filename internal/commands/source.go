package commands

import (
	"fmt"
	"strings"
	"time"

	"gioui.org/app"
	"github.com/kfsone/mucka/internal/ui"
)

// SourceDelay is the delay between token injections during $source playback.
var SourceDelay = 80 * time.Millisecond

type sourceOp struct {
	kind  string
	value string
}

// sourceTokens converts a $source file line into a slice of (kind, value) pairs.
// Special tokens: {enter} → submit, {bs} → backspace, {clear} → clear.
// Other tokens → opText.
func sourceTokens(line string) []sourceOp {
	tokens := strings.Fields(line)
	var ops []sourceOp
	for _, tok := range tokens {
		switch tok {
		case "{enter}":
			ops = append(ops, sourceOp{ui.OpSubmit, ""})
		case "{bs}":
			ops = append(ops, sourceOp{ui.OpBS, ""})
		case "{clear}":
			ops = append(ops, sourceOp{ui.OpClear, ""})
		default:
			ops = append(ops, sourceOp{ui.OpText, tok})
		}
	}
	return ops
}

// sourceHandler returns a HandlerFunc that replays file tokens into the InputLine.
func sourceHandler(w *app.Window, panel *ui.TextPanel, il *ui.InputLine) HandlerFunc {
	return func(args []string) {
		if len(args) == 0 {
			panel.AppendText("$source: filename required")
			return
		}
		filename := args[0]
		lines, err := readFileLines(filename)
		if err != nil {
			panel.AppendText(fmt.Sprintf("$source: %v", err))
			return
		}
		go func() {
			for _, line := range lines {
				for _, op := range sourceTokens(line) {
					il.EnqueueOp(op.kind, op.value)
					w.Invalidate()
					time.Sleep(SourceDelay)
				}
				// Implicit Enter at end of each line.
				il.EnqueueOp(ui.OpSubmit, "")
				w.Invalidate()
				time.Sleep(SourceDelay)
			}
		}()
	}
}
