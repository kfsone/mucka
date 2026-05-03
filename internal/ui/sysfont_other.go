//go:build !windows

package ui

import (
	"gioui.org/font/gofont"
	"gioui.org/text"
)

// LoadFontCollection falls back to the bundled Go fonts on non-Windows platforms.
func LoadFontCollection(fontName string) []text.FontFace {
	return gofont.Collection()
}
