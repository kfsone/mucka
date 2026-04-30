package ui

import (
	"image/color"
	"strings"
	"testing"

	"gioui.org/layout"
	"gioui.org/widget/material"

	"github.com/kfsone/mucka/internal/fes"
)

// Compile-time check: StatusBar.Layout must accept (layout.Context,
// *material.Theme, connecting bool, connected bool) and return layout.Dimensions.
// If the signature changes this file will fail to compile, acting as a
// lightweight contract test for the status-bar interface.
var _statusBarInstance StatusBar
var _ func(layout.Context, *material.Theme, bool, bool) layout.Dimensions = _statusBarInstance.Layout

func TestRatioColor_Green(t *testing.T) {
	// max == 0 → green (no data)
	c := ratioColor(0, 0)
	want := color.NRGBA{R: 0x00, G: 0xCC, B: 0x00, A: 0xFF}
	if c != want {
		t.Errorf("ratioColor(0,0) = %v, want green %v", c, want)
	}

	// 100% → green
	c = ratioColor(30, 30)
	if c != want {
		t.Errorf("ratioColor(30,30) = %v, want green %v", c, want)
	}

	// 75% → green (boundary)
	c = ratioColor(75, 100)
	if c != want {
		t.Errorf("ratioColor(75,100) = %v, want green %v", c, want)
	}
}

func TestRatioColor_Yellow(t *testing.T) {
	want := color.NRGBA{R: 0xCC, G: 0xCC, B: 0x00, A: 0xFF}

	// 74% → yellow
	c := ratioColor(74, 100)
	if c != want {
		t.Errorf("ratioColor(74,100) = %v, want yellow %v", c, want)
	}

	// 40% → yellow (boundary)
	c = ratioColor(40, 100)
	if c != want {
		t.Errorf("ratioColor(40,100) = %v, want yellow %v", c, want)
	}
}

func TestRatioColor_CurExceedsMax(t *testing.T) {
	// cur > max (over-healed / buffed) should still be treated as ≥75% → green.
	want := color.NRGBA{R: 0x00, G: 0xCC, B: 0x00, A: 0xFF}
	c := ratioColor(110, 100)
	if c != want {
		t.Errorf("ratioColor(110,100) = %v, want green %v", c, want)
	}
}

func TestRatioColor_Red(t *testing.T) {
	want := color.NRGBA{R: 0xCC, G: 0x00, B: 0x00, A: 0xFF}

	// 39% → red
	c := ratioColor(39, 100)
	if c != want {
		t.Errorf("ratioColor(39,100) = %v, want red %v", c, want)
	}

	// 0/max → red
	c = ratioColor(0, 10)
	if c != want {
		t.Errorf("ratioColor(0,10) = %v, want red %v", c, want)
	}
}

func TestStatusBar_SetStats_nil(t *testing.T) {
	sb := NewStatusBar()
	sb.SetStats(nil)
	if sb.stats.Load() != nil {
		t.Error("stats.Load() should be nil after SetStats(nil)")
	}
}

func TestStatusBar_SetStats(t *testing.T) {
	sb := NewStatusBar()
	s := &fes.Stats{
		Stamina:    20,
		MaxStamina: 30,
		Rank:       "Hero",
		Score:      500,
	}
	sb.SetStats(s)
	got := sb.stats.Load()
	if got == nil {
		t.Fatal("stats should not be nil after SetStats")
	}
	if got == s {
		t.Error("SetStats should store a copy, not the original pointer")
	}
	if got.Stamina != 20 {
		t.Errorf("Stamina = %d, want 20", got.Stamina)
	}
	if got.Rank != "Hero" {
		t.Errorf("Rank = %q, want Hero", got.Rank)
	}
	if got.Score != 500 {
		t.Errorf("Score = %d, want 500", got.Score)
	}

	// Mutating the original should not affect the stored copy.
	s.Stamina = 999
	if sb.stats.Load().Stamina == 999 {
		t.Error("stored copy was mutated when original changed")
	}
}

func TestBuildStatParts(t *testing.T) {
	st := &fes.Stats{
		Stamina:      25,
		MaxStamina:   30,
		Strength:     10,
		MaxStrength:  15,
		Dexterity:    8,
		MaxDexterity: 12,
		Magic:        3,
		MaxMagic:     30,
		Score:        12345,
		Rank:         "Warlock",
		Level:        0,
	}
	parts := buildStatParts(st)
	if len(parts) == 0 {
		t.Fatal("buildStatParts returned empty slice")
	}
	// First part should be the stamina heart symbol label.
	if parts[0].text != "♥ " {
		t.Errorf("parts[0].text = %q, want \"♥ \"", parts[0].text)
	}
	// Score should appear as icon "  ★ " followed by the formatted number.
	found := false
	for i, p := range parts {
		if p.text == "  ★ " && i+1 < len(parts) && parts[i+1].text == "12,345" {
			found = true
		}
	}
	if !found {
		t.Error("score segments \"  ★ \"/\"12,345\" not found in stat parts")
	}
	// Rank should appear as "  Warlock".
	found = false
	for _, p := range parts {
		if p.text == "  Warlock" {
			found = true
		}
	}
	if !found {
		t.Error("rank segment \"  Warlock\" not found in stat parts")
	}
}

func TestBuildStatParts_NoRank(t *testing.T) {
	st := &fes.Stats{Stamina: 5, MaxStamina: 10}
	parts := buildStatParts(st)
	if len(parts) == 0 {
		t.Fatal("buildStatParts returned empty slice")
	}
	// Without a rank the first part should be the stamina heart symbol label.
	if parts[0].text != "♥ " {
		t.Errorf("first part text = %q, want \"♥ \"", parts[0].text)
	}
}

func TestBuildStatParts_StrAlwaysCurMax(t *testing.T) {
	// STR must always show "cur/max" even when cur == max.
	st := &fes.Stats{
		Stamina:     10,
		MaxStamina:  10,
		Strength:    15,
		MaxStrength: 15,
	}
	parts := buildStatParts(st)
	for i, p := range parts {
		if p.text == "  S " && i+1 < len(parts) {
			val := parts[i+1].text
			if val != "15/15" {
				t.Errorf("STR cur/max = %q, want \"15/15\"", val)
			}
			return
		}
	}
	t.Error("strength label \"  S \" not found")
}

func TestBuildStatParts_RankNoLevel(t *testing.T) {
	// Rank segment must show the rank name only, never a level number.
	st := &fes.Stats{
		Stamina:    20,
		MaxStamina: 20,
		Rank:       "Knight",
		Level:      7,
	}
	parts := buildStatParts(st)
	found := false
	for _, p := range parts {
		if p.text == "  Knight" {
			found = true
		}
		if p.text == "  Knight Lv7" {
			t.Error("rank segment must not include level: got \"  Knight Lv7\"")
		}
	}
	if !found {
		t.Error("rank segment \"  Knight\" not found")
	}
}

func TestBuildStatParts_HidesMagic(t *testing.T) {
	// When MaxMagic == 0, the MAG segment must not appear.
	st := &fes.Stats{
		Stamina:    30,
		MaxStamina: 100,
		MaxMagic:   0,
	}
	parts := buildStatParts(st)
	for _, p := range parts {
		if p.text == "  M " {
			t.Error("MAG label should be hidden when MaxMagic == 0")
		}
	}
}

func TestBuildStatParts_RankLevel(t *testing.T) {
	// Rank should appear as "  hero" (no level number).
	st := &fes.Stats{
		Stamina:    30,
		MaxStamina: 100,
		Rank:       "hero",
		Level:      5,
	}
	parts := buildStatParts(st)
	found := false
	for _, p := range parts {
		if p.text == "  hero" {
			found = true
		}
	}
	if !found {
		t.Error("rank segment \"  hero\" not found in stat parts")
	}
	// Level number must NOT appear.
	for _, p := range parts {
		if p.text == "  hero Lv5" {
			t.Error("rank segment must not include level number")
		}
	}
}

func TestBuildStatParts_StrengthSingleValue(t *testing.T) {
	// Strength must always show cur/max, even when equal.
	st := &fes.Stats{
		Stamina:     30,
		MaxStamina:  100,
		Strength:    100,
		MaxStrength: 100,
	}
	parts := buildStatParts(st)
	for i, p := range parts {
		if p.text == "  S " && i+1 < len(parts) {
			val := parts[i+1].text
			if val != "100/100" {
				t.Errorf("equal strength value = %q, want \"100/100\"", val)
			}
			return
		}
	}
	t.Error("strength label \"  S \" not found in stat parts")
}

func TestBuildStatParts_DexteritySingleValue(t *testing.T) {
	// Dexterity must always show cur/max, even when equal.
	st := &fes.Stats{
		Stamina:      30,
		MaxStamina:   100,
		Dexterity:    80,
		MaxDexterity: 80,
	}
	parts := buildStatParts(st)
	for i, p := range parts {
		if p.text == "  D " && i+1 < len(parts) {
			val := parts[i+1].text
			if val != "80/80" {
				t.Errorf("equal dexterity value = %q, want \"80/80\"", val)
			}
			return
		}
	}
	t.Error("dexterity label \"  D \" not found in stat parts")
}

func TestBuildStatParts_NoLvSubstringAnywhere(t *testing.T) {
	// No stat part may contain "Lv" anywhere — rank is name-only.
	cases := []*fes.Stats{
		{Stamina: 10, MaxStamina: 10, Rank: "Knight", Level: 7},
		{Stamina: 10, MaxStamina: 10, Rank: "Warlock", Level: 1},
		{Stamina: 10, MaxStamina: 10, Rank: "hero", Level: 99},
		{Stamina: 10, MaxStamina: 10, Rank: "", Level: 5},
	}
	for _, st := range cases {
		for _, p := range buildStatParts(st) {
			if strings.Contains(p.text, "Lv") {
				t.Errorf("stat part %q contains \"Lv\" (rank=%q level=%d)", p.text, st.Rank, st.Level)
			}
		}
	}
}

func TestBuildStatParts_EmptyRankHidesSegment(t *testing.T) {
	// When Rank is empty the rank segment must be absent, even when Level > 0.
	st := &fes.Stats{
		Stamina:    30,
		MaxStamina: 100,
		Rank:       "",
		Level:      5,
	}
	parts := buildStatParts(st)
	for _, p := range parts {
		// Any segment starting with two spaces that is not a known label is suspect.
		if p.text == "  " || (len(p.text) > 2 && p.text[:2] == "  " &&
			p.text != "  S " && p.text != "  D " && p.text != "  M " && p.text != "  ★ ") {
			t.Errorf("unexpected rank segment when Rank is empty: %q", p.text)
		}
	}
}

func TestStatusBar_SetDreamWord_stored(t *testing.T) {
	sb := NewStatusBar()
	sb.SetDreamWord("frog")
	got := sb.dreamWord.Load()
	if got == nil {
		t.Fatal("dreamWord should not be nil after SetDreamWord(\"frog\")")
	}
	if *got != "frog" {
		t.Errorf("dreamWord = %q, want %q", *got, "frog")
	}
}

func TestStatusBar_SetDreamWord_clear(t *testing.T) {
	sb := NewStatusBar()
	sb.SetDreamWord("frog")
	sb.SetDreamWord("")
	got := sb.dreamWord.Load()
	if got != nil {
		t.Errorf("dreamWord should be nil after SetDreamWord(\"\"), got %q", *got)
	}
}

// ── Reset-minutes feature tests ───────────────────────────────────────────

func TestSetStats_CopiesResetMinutes(t *testing.T) {
	sb := NewStatusBar()
	s := &fes.Stats{ResetMinutes: 44}
	sb.SetStats(s)
	got := sb.stats.Load()
	if got == nil {
		t.Fatal("stats must not be nil after SetStats")
	}
	if got.ResetMinutes != 44 {
		t.Errorf("ResetMinutes = %d, want 44", got.ResetMinutes)
	}
	// Mutating original must not affect stored copy.
	s.ResetMinutes = 0
	if sb.stats.Load().ResetMinutes != 44 {
		t.Error("stored copy was mutated when original changed")
	}
}

func TestSetStats_ZeroResetMinutesCopied(t *testing.T) {
	sb := NewStatusBar()
	sb.SetStats(&fes.Stats{ResetMinutes: 5})
	// Now set stats with zero ResetMinutes.
	sb.SetStats(&fes.Stats{ResetMinutes: 0})
	got := sb.stats.Load()
	if got.ResetMinutes != 0 {
		t.Errorf("ResetMinutes = %d, want 0", got.ResetMinutes)
	}
}

func TestBuildStatParts_DoesNotIncludeResetMinutes(t *testing.T) {
	// buildStatParts must not embed the reset-minutes text; Layout adds it separately.
	st := &fes.Stats{
		Stamina:      30,
		MaxStamina:   100,
		ResetMinutes: 44,
	}
	parts := buildStatParts(st)
	for _, p := range parts {
		if strings.Contains(p.text, "44m") {
			t.Errorf("buildStatParts must not include reset-minutes text; found %q", p.text)
		}
		if strings.Contains(p.text, "44") {
			// Score or stat values could legitimately contain "44", but no segment
			// should look like the reset timer ("Xm ").
			if len(p.text) >= 3 && p.text[len(p.text)-2:] == "m " {
				t.Errorf("segment %q looks like a reset-timer label in buildStatParts", p.text)
			}
		}
	}
}

func TestNeutralColor(t *testing.T) {
	// neutralColor must be a light greenish color (used for reset timer and labels).
	want := color.NRGBA{R: 0xCC, G: 0xFF, B: 0xCC, A: 0xFF}
	if neutralColor != want {
		t.Errorf("neutralColor = %v, want %v", neutralColor, want)
	}
}
