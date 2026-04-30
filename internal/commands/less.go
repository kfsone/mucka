package commands

import (
	"fmt"
)

// LessPageSize is the number of lines displayed per page by $less.
var LessPageSize = 20

// paginateLines splits lines into pages of pageSize.
func paginateLines(lines []string, pageSize int) [][]string {
	if len(lines) == 0 {
		return nil
	}
	var pages [][]string
	for i := 0; i < len(lines); i += pageSize {
		end := i + pageSize
		if end > len(lines) {
			end = len(lines)
		}
		pages = append(pages, lines[i:end])
	}
	return pages
}

// lessHandler returns a HandlerFunc for $less.
func lessHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		if len(args) == 0 {
			d.u.TextPanel.AppendText("$less: filename required")
			return
		}
		filename := args[0]
		lines, err := readFileLines(filename)
		if err != nil {
			d.u.TextPanel.AppendText(fmt.Sprintf("$less: %v", err))
			return
		}
		pages := paginateLines(lines, LessPageSize)
		if len(pages) == 0 {
			d.u.TextPanel.AppendText("$less: empty file")
			return
		}
		// Show the first page synchronously (we are on the main goroutine).
		for _, line := range pages[0] {
			d.u.TextPanel.AppendText(line)
		}
		if len(pages) == 1 {
			d.u.TextPanel.AppendText("-- END --")
			return
		}
		// Enter less mode for the remaining pages.
		d.enterLessMode(pages[1:])
	}
}


