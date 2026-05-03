package mud2

import (
	"image/color"
	"strconv"
	"sync"

	"github.com/kfsone/mucka/internal/ansi"
)

// colorKey is a composite key for (FG, BG) color pair lookups.
type colorKey struct {
	FG, BG color.NRGBA
}

// ColorMap maps (FG, BG) ANSI color pairs to MUD2 semantic type numbers.
// It is built from /AL responses sent by the server at game entry.
// The zero value is not valid; use NewColorMap.
// Safe for concurrent use.
type ColorMap struct {
	mu sync.RWMutex
	m  map[colorKey]int
}

// NewColorMap returns an empty ColorMap ready for use.
func NewColorMap() *ColorMap {
	return &ColorMap{m: make(map[colorKey]int)}
}

// Set associates the (fg, bg) color pair with the given semantic type number.
func (m *ColorMap) Set(fg, bg color.NRGBA, typeNum int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.m[colorKey{fg, bg}] = typeNum
}

// Lookup returns the semantic type number for the given (fg, bg) color pair,
// or 0 if the pair is not in the map.
func (m *ColorMap) Lookup(fg, bg color.NRGBA) int {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.m[colorKey{fg, bg}]
}

// ParseALLine parses a single /AL response line of the form "/ASfbN" where
// f is the foreground color letter, b is the background color letter, and N
// is the semantic type number (0–60). Updates the map and returns true on
// success. Returns false for lines that do not match the expected format.
func (m *ColorMap) ParseALLine(line string) bool {
	// Minimum: "/AS" (3) + fg letter (1) + bg letter (1) + at least one digit (1) = 6
	if len(line) < 6 || line[0] != '/' || line[1] != 'A' || line[2] != 'S' {
		return false
	}
	fg, fgOK := letterToColor(line[3], ansi.DefaultFG)
	bg, bgOK := letterToColor(line[4], ansi.DefaultBG)
	if !fgOK || !bgOK {
		return false
	}
	typeNum, err := strconv.Atoi(line[5:])
	if err != nil || typeNum < 0 || typeNum > 60 {
		return false
	}
	m.Set(fg, bg, typeNum)
	return true
}

// letterToColor converts an /AL color letter to its NRGBA value.
// defaultColor is returned for the letter 'n' (normal/default) and allows
// callers to supply the correct default for foreground vs background context.
// Returns the color and true on success, color.NRGBA{} and false for unknown letters.
func letterToColor(c byte, defaultColor color.NRGBA) (color.NRGBA, bool) {
	switch c {
	case 'n':
		return defaultColor, true
	case 'K':
		return ansi.StandardColor(0), true // black
	case 'R':
		return ansi.StandardColor(1), true // red
	case 'G':
		return ansi.StandardColor(2), true // green
	case 'Y':
		return ansi.StandardColor(3), true // yellow
	case 'B':
		return ansi.StandardColor(4), true // blue
	case 'M':
		return ansi.StandardColor(5), true // magenta
	case 'C':
		return ansi.StandardColor(6), true // cyan
	case 'W':
		return ansi.StandardColor(7), true // white
	case 'k':
		return ansi.BrightColor(0), true // bright black
	case 'r':
		return ansi.BrightColor(1), true // bright red
	case 'g':
		return ansi.BrightColor(2), true // bright green
	case 'y':
		return ansi.BrightColor(3), true // bright yellow
	case 'b':
		return ansi.BrightColor(4), true // bright blue
	case 'm':
		return ansi.BrightColor(5), true // bright magenta
	case 'c':
		return ansi.BrightColor(6), true // bright cyan
	case 'w':
		return ansi.BrightColor(7), true // bright white
	}
	return color.NRGBA{}, false
}
