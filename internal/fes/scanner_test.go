package fes

import "testing"

func TestScanLine_NoMatch(t *testing.T) {
	var s Stats
	if ScanLine("The quick brown fox", &s) {
		t.Error("should not match")
	}
	if ScanLine("", &s) {
		t.Error("empty line should not match")
	}
}

func TestScanLine_Stamina_SlashFormat(t *testing.T) {
	var s Stats
	if !ScanLine("Stamina: 25/30", &s) {
		t.Fatal("should have matched")
	}
	if s.Stamina != 25 {
		t.Errorf("Stamina = %d, want 25", s.Stamina)
	}
	if s.MaxStamina != 30 {
		t.Errorf("MaxStamina = %d, want 30", s.MaxStamina)
	}
}

func TestScanLine_Stamina_SingleValue(t *testing.T) {
	var s Stats
	if !ScanLine("stamina: 18", &s) {
		t.Fatal("should have matched")
	}
	if s.Stamina != 18 {
		t.Errorf("Stamina = %d, want 18", s.Stamina)
	}
	// MaxStamina should be untouched
	if s.MaxStamina != 0 {
		t.Errorf("MaxStamina = %d, want 0", s.MaxStamina)
	}
}

func TestScanLine_Stamina_CaseInsensitive(t *testing.T) {
	var s Stats
	if !ScanLine("STAMINA: 10/20", &s) {
		t.Fatal("should have matched case-insensitively")
	}
	if s.Stamina != 10 || s.MaxStamina != 20 {
		t.Errorf("got %d/%d, want 10/20", s.Stamina, s.MaxStamina)
	}
}

func TestScanLine_Strength(t *testing.T) {
	var s Stats
	if !ScanLine("Strength: 15", &s) {
		t.Fatal("should have matched")
	}
	if s.MaxStrength != 15 {
		t.Errorf("MaxStrength = %d, want 15", s.MaxStrength)
	}
	// Without "effective strength:" on the same line, Strength defaults to MaxStrength.
	if s.Strength != 15 {
		t.Errorf("Strength = %d, want 15 (equals MaxStrength)", s.Strength)
	}
}

func TestScanLine_EffectiveStrength(t *testing.T) {
	var s Stats
	if !ScanLine("Effective strength: 12", &s) {
		t.Fatal("should have matched")
	}
	if s.Strength != 12 {
		t.Errorf("Strength = %d, want 12", s.Strength)
	}
	// MaxStrength must NOT be updated by the "effective strength:" line
	if s.MaxStrength != 0 {
		t.Errorf("MaxStrength should be untouched, got %d", s.MaxStrength)
	}
}

func TestScanLine_StrengthNotEffective(t *testing.T) {
	// Both "effective strength:" and a standalone "strength:" on the same line
	// is unusual but must be handled correctly: effective → Strength, non-effective → MaxStrength.
	var s Stats
	ScanLine("effective strength: 12 strength: 18", &s)
	if s.Strength != 12 {
		t.Errorf("Strength = %d, want 12", s.Strength)
	}
	if s.MaxStrength != 18 {
		t.Errorf("MaxStrength = %d, want 18", s.MaxStrength)
	}
}

func TestScanLine_Dexterity(t *testing.T) {
	var s Stats
	if !ScanLine("Dexterity: 9", &s) {
		t.Fatal("should have matched")
	}
	if s.MaxDexterity != 9 {
		t.Errorf("MaxDexterity = %d, want 9", s.MaxDexterity)
	}
	// Without "effective dexterity:" on the same line, Dexterity defaults to MaxDexterity.
	if s.Dexterity != 9 {
		t.Errorf("Dexterity = %d, want 9 (equals MaxDexterity)", s.Dexterity)
	}
}

func TestScanLine_EffectiveDexterity(t *testing.T) {
	var s Stats
	if !ScanLine("effective dexterity: 7", &s) {
		t.Fatal("should have matched")
	}
	if s.Dexterity != 7 {
		t.Errorf("Dexterity = %d, want 7", s.Dexterity)
	}
	if s.MaxDexterity != 0 {
		t.Errorf("MaxDexterity should be untouched, got %d", s.MaxDexterity)
	}
}

func TestScanLine_Score(t *testing.T) {
	var s Stats
	if !ScanLine("Score: 98765", &s) {
		t.Fatal("should have matched")
	}
	if s.Score != 98765 {
		t.Errorf("Score = %d, want 98765", s.Score)
	}
}

func TestScanLine_Level(t *testing.T) {
	var s Stats
	if !ScanLine("Level: Warlock", &s) {
		t.Fatal("should have matched")
	}
	if s.Rank != "Warlock" {
		t.Errorf("Rank = %q, want Warlock", s.Rank)
	}
}

func TestScanLine_YourStaminaIs(t *testing.T) {
	var s Stats
	if !ScanLine("Your stamina is 22", &s) {
		t.Fatal("should have matched")
	}
	if s.Stamina != 22 {
		t.Errorf("Stamina = %d, want 22", s.Stamina)
	}
}

func TestScanLine_PersonaSaved(t *testing.T) {
	var s Stats
	line := "(Persona saved on Monday. score: 54321)"
	if !ScanLine(line, &s) {
		t.Fatal("should have matched")
	}
	if s.Score != 54321 {
		t.Errorf("Score = %d, want 54321", s.Score)
	}
}

func TestScanLine_PersonaSaved_NoScore(t *testing.T) {
	// "(Persona saved on" present but no "score" in line
	var s Stats
	if ScanLine("(Persona saved on Wednesday.)", &s) {
		// Score not set — still returns false (no update)
		t.Error("should not match without a score field")
	}
}

func TestScanLine_MultipleFields(t *testing.T) {
	// A single line containing multiple patterns
	var s Stats
	line := "Stamina: 15/25 Strength: 10"
	if !ScanLine(line, &s) {
		t.Fatal("should have matched")
	}
	if s.Stamina != 15 {
		t.Errorf("Stamina = %d, want 15", s.Stamina)
	}
	if s.MaxStamina != 25 {
		t.Errorf("MaxStamina = %d, want 25", s.MaxStamina)
	}
	if s.MaxStrength != 10 {
		t.Errorf("MaxStrength = %d, want 10", s.MaxStrength)
	}
}

func TestScanLine_PartialLine(t *testing.T) {
	// Line contains "stamina:" but no number after it
	var s Stats
	if ScanLine("stamina:", &s) {
		t.Error("should not match — no number after colon")
	}
}

func TestScanLine_DexterityNotEffective(t *testing.T) {
	// Both "effective dexterity:" and a standalone "dexterity:" on the same line:
	// effective → Dexterity, non-effective → MaxDexterity.
	var s Stats
	ScanLine("effective dexterity: 7 dexterity: 12", &s)
	if s.Dexterity != 7 {
		t.Errorf("Dexterity = %d, want 7", s.Dexterity)
	}
	if s.MaxDexterity != 12 {
		t.Errorf("MaxDexterity = %d, want 12", s.MaxDexterity)
	}
}

// --- Tests for bug fixes exposed by real MUD2 output ---

func TestScanLine_Stamina_MaxFormat(t *testing.T) {
	// Real MUD2 output: "stamina:        30      max:    100"
	var s Stats
	if !ScanLine("stamina:        30      max:    100", &s) {
		t.Fatal("should have matched")
	}
	if s.Stamina != 30 {
		t.Errorf("Stamina = %d, want 30", s.Stamina)
	}
	if s.MaxStamina != 100 {
		t.Errorf("MaxStamina = %d, want 100", s.MaxStamina)
	}
}

func TestScanLine_Strength_NoEffective(t *testing.T) {
	// "strength:       100" alone → Strength=MaxStrength=100.
	var s Stats
	if !ScanLine("strength:       100", &s) {
		t.Fatal("should have matched")
	}
	if s.MaxStrength != 100 {
		t.Errorf("MaxStrength = %d, want 100", s.MaxStrength)
	}
	if s.Strength != 100 {
		t.Errorf("Strength = %d, want 100", s.Strength)
	}
}

func TestScanLine_Dexterity_WithEffective(t *testing.T) {
	// Real MUD2 output: "dexterity:      95      effective dexterity:    92"
	var s Stats
	if !ScanLine("dexterity:      95      effective dexterity:    92", &s) {
		t.Fatal("should have matched")
	}
	if s.MaxDexterity != 95 {
		t.Errorf("MaxDexterity = %d, want 95", s.MaxDexterity)
	}
	if s.Dexterity != 92 {
		t.Errorf("Dexterity = %d, want 92", s.Dexterity)
	}
}

func TestScanLine_Score_WithCommas(t *testing.T) {
	// Real MUD2 output: "score:  5,184 points"
	var s Stats
	if !ScanLine("score:  5,184 points", &s) {
		t.Fatal("should have matched")
	}
	if s.Score != 5184 {
		t.Errorf("Score = %d, want 5184", s.Score)
	}
}

func TestScanLine_Level_NumericWithRank(t *testing.T) {
	// Real MUD2 output: "level:  5       hero"
	var s Stats
	if !ScanLine("level:  5       hero", &s) {
		t.Fatal("should have matched")
	}
	if s.Level != 5 {
		t.Errorf("Level = %d, want 5", s.Level)
	}
	if s.Rank != "hero" {
		t.Errorf("Rank = %q, want hero", s.Rank)
	}
}

func TestScanLine_Level_NumericOnly(t *testing.T) {
	// "level: 7" with no rank token — Level updated, Rank unchanged.
	s := Stats{Rank: "wizard"}
	if !ScanLine("level:  7", &s) {
		t.Fatal("should have matched")
	}
	if s.Level != 7 {
		t.Errorf("Level = %d, want 7", s.Level)
	}
	// Rank must not be overwritten when no rank token is present.
	if s.Rank != "wizard" {
		t.Errorf("Rank = %q, want wizard (unchanged)", s.Rank)
	}
}

func TestScanLine_EffectiveStrength_SeparateLines(t *testing.T) {
	// "strength: 100" on one line → Strength=MaxStrength=100.
	// A subsequent "effective strength: 90" must override only Strength,
	// leaving MaxStrength untouched.
	var s Stats
	ScanLine("strength:       100", &s)
	if s.MaxStrength != 100 || s.Strength != 100 {
		t.Fatalf("after strength line: MaxStrength=%d Strength=%d, want both 100",
			s.MaxStrength, s.Strength)
	}
	ScanLine("effective strength:      90", &s)
	if s.Strength != 90 {
		t.Errorf("Strength = %d, want 90 (effective override)", s.Strength)
	}
	if s.MaxStrength != 100 {
		t.Errorf("MaxStrength = %d, want 100 (must not change)", s.MaxStrength)
	}
}

func TestScanLine_EffectiveDexterity_SeparateLines(t *testing.T) {
	// Same cross-call correctness for dexterity.
	var s Stats
	ScanLine("dexterity:      95", &s)
	if s.MaxDexterity != 95 || s.Dexterity != 95 {
		t.Fatalf("after dexterity line: MaxDexterity=%d Dexterity=%d, want both 95",
			s.MaxDexterity, s.Dexterity)
	}
	ScanLine("effective dexterity:     92", &s)
	if s.Dexterity != 92 {
		t.Errorf("Dexterity = %d, want 92 (effective override)", s.Dexterity)
	}
	if s.MaxDexterity != 95 {
		t.Errorf("MaxDexterity = %d, want 95 (must not change)", s.MaxDexterity)
	}
}

func TestScanLine_PersonaSaved_WithCommas(t *testing.T) {
	// "(Persona saved on" line with a comma-formatted score.
	var s Stats
	line := "(Persona saved on Tuesday. score: 1,234,567)"
	if !ScanLine(line, &s) {
		t.Fatal("should have matched")
	}
	if s.Score != 1234567 {
		t.Errorf("Score = %d, want 1234567", s.Score)
	}
}
