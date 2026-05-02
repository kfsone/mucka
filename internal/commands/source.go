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
// Everything else (including spaces) is emitted as a single OpText span.
func sourceTokens(line string) []sourceOp {
	var ops []sourceOp
	for {
		start := strings.IndexByte(line, '{')
		if start < 0 {
			if line != "" {
				ops = append(ops, sourceOp{ui.OpText, line})
			}
			break
		}
		if start > 0 {
			ops = append(ops, sourceOp{ui.OpText, line[:start]})
		}
		end := strings.IndexByte(line[start:], '}')
		if end < 0 {
			ops = append(ops, sourceOp{ui.OpText, line[start:]})
			break
		}
		end += start + 1
		switch line[start:end] {
		case "{enter}":
			ops = append(ops, sourceOp{ui.OpSubmit, ""})
		case "{bs}":
			ops = append(ops, sourceOp{ui.OpBS, ""})
		case "{clear}":
			ops = append(ops, sourceOp{ui.OpClear, ""})
		default:
			ops = append(ops, sourceOp{ui.OpText, line[start:end]})
		}
		line = line[end:]
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
