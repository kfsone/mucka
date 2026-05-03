package commands

import (
	"bufio"
	"fmt"
	"os"
	"strings"
	"time"

	"gioui.org/app"
	"github.com/kfsone/mucka/internal/ui"
)

// StreamDelay is the delay between lines during $stream playback.
var StreamDelay = 50 * time.Millisecond

// readFileLines reads non-empty, non-comment lines from filename.
// Lines starting with '#' are skipped. Returns an error if the file cannot be opened.
func readFileLines(filename string) ([]string, error) {
	f, err := os.Open(filename)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	var lines []string
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := scanner.Text()
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		lines = append(lines, line)
	}
	return lines, scanner.Err()
}

// streamHandler returns a HandlerFunc that streams the given file to the panel.
func streamHandler(w *app.Window, panel *ui.TextPanel, d *Dispatcher) HandlerFunc {
	return func(args []string) {
		if len(args) == 0 {
			panel.AppendText("$stream: filename required")
			return
		}
		filename := args[0]
		lines, err := readFileLines(filename)
		if err != nil {
			panel.AppendText(fmt.Sprintf("$stream: %v", err))
			return
		}
		ctx := d.newStreamCtx()
		go func() {
			for _, rawLine := range lines {
				panel.AppendText(unescapeStreamLine(rawLine))
				w.Invalidate()
				select {
				case <-ctx.Done():
					return
				case <-time.After(StreamDelay):
				}
			}
		}()
	}
}
