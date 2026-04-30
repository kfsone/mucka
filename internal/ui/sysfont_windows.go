package ui

import (
	"os"
	"path/filepath"
	"strings"

	"gioui.org/font/gofont"
	"gioui.org/font/opentype"
	"gioui.org/text"
)

// LoadFontCollection attempts to load fontName from Windows system/user font
// directories. Falls back to gofont.Collection() if the font cannot be found
// or parsed.
func LoadFontCollection(fontName string) []text.FontFace {
	base := gofont.Collection()
	if fontName == "" {
		return base
	}
	// Convert display name to a likely filename: "Cascadia Mono" → "CascadiaMono"
	stem := strings.ReplaceAll(fontName, " ", "")
	candidates := []string{
		filepath.Join(os.Getenv("WINDIR"), "Fonts", stem+".ttf"),
		filepath.Join(os.Getenv("WINDIR"), "Fonts", stem+".otf"),
		filepath.Join(os.Getenv("LOCALAPPDATA"), "Microsoft", "Windows", "Fonts", stem+".ttf"),
		filepath.Join(os.Getenv("LOCALAPPDATA"), "Microsoft", "Windows", "Fonts", stem+".otf"),
	}
	for _, p := range candidates {
		data, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		faces, err := opentype.ParseCollection(data)
		if err != nil {
			continue
		}
		return append(faces, base...)
	}
	return base
}
