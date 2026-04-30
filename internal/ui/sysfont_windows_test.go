package ui_test

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/kfsone/mucka/internal/ui"
)

// TestLoadFontCollection_EmptyName verifies that an empty font name returns
// a non-nil, non-empty collection (gofont fallback).
func TestLoadFontCollection_EmptyName(t *testing.T) {
	faces := ui.LoadFontCollection("")
	if len(faces) == 0 {
		t.Error("empty fontName: want non-empty collection, got empty")
	}
}

// TestLoadFontCollection_UnknownFont verifies that an unrecognised font name
// falls back to gofont rather than returning nil or an empty collection.
func TestLoadFontCollection_UnknownFont(t *testing.T) {
	faces := ui.LoadFontCollection("ThisFontDefinitelyDoesNotExist99999")
	if len(faces) == 0 {
		t.Error("unknown font: want gofont fallback (non-empty), got empty collection")
	}
}

// TestLoadFontCollection_FallbackConsistency verifies that the collection
// returned for an unknown font is the same size as requesting an empty name
// (both should be pure gofont).
func TestLoadFontCollection_FallbackConsistency(t *testing.T) {
	base := ui.LoadFontCollection("")
	fallback := ui.LoadFontCollection("AbsolutelyNonexistentFont_XYZ")
	if len(base) != len(fallback) {
		t.Errorf("fallback size mismatch: empty=%d, unknown=%d", len(base), len(fallback))
	}
}

// TestLoadFontCollection_SpaceRemovedInStem verifies that a font name with
// spaces is searched as a space-free stem. We can't control what's installed,
// but we can confirm the function doesn't panic and returns a valid collection.
func TestLoadFontCollection_SpaceRemovedInStem(t *testing.T) {
	faces := ui.LoadFontCollection("Some Font With Spaces")
	if faces == nil {
		t.Error("want non-nil collection even for spaced font name not found")
	}
}

// TestLoadFontCollection_ExistingTTF verifies that if a .ttf file exists in
// the Windows Fonts directory it is loaded and the collection is larger than
// the gofont baseline. Skips if WINDIR is unset or the standard Arial font
// is not present (unusual but possible in CI).
func TestLoadFontCollection_ExistingTTF(t *testing.T) {
	windir := os.Getenv("WINDIR")
	if windir == "" {
		t.Skip("WINDIR not set")
	}
	// Arial is present on virtually every Windows installation.
	arialPath := filepath.Join(windir, "Fonts", "arial.ttf")
	if _, err := os.Stat(arialPath); err != nil {
		t.Skipf("arial.ttf not found at %s: %v", arialPath, err)
	}

	base := ui.LoadFontCollection("")
	faces := ui.LoadFontCollection("Arial")
	// The loaded collection must include at least the gofont faces plus the
	// newly loaded Arial faces, so it should be strictly larger.
	if len(faces) <= len(base) {
		t.Errorf("expected collection larger than gofont base (%d); got %d", len(base), len(faces))
	}
}
