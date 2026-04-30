package fes

import "testing"

func TestStats_ZeroValue(t *testing.T) {
	var s Stats
	if s.Stamina != 0 {
		t.Errorf("Stamina = %d, want 0", s.Stamina)
	}
	if s.MaxStamina != 0 {
		t.Errorf("MaxStamina = %d, want 0", s.MaxStamina)
	}
	if s.Strength != 0 {
		t.Errorf("Strength = %d, want 0", s.Strength)
	}
	if s.MaxStrength != 0 {
		t.Errorf("MaxStrength = %d, want 0", s.MaxStrength)
	}
	if s.Dexterity != 0 {
		t.Errorf("Dexterity = %d, want 0", s.Dexterity)
	}
	if s.MaxDexterity != 0 {
		t.Errorf("MaxDexterity = %d, want 0", s.MaxDexterity)
	}
	if s.Magic != 0 {
		t.Errorf("Magic = %d, want 0", s.Magic)
	}
	if s.MaxMagic != 0 {
		t.Errorf("MaxMagic = %d, want 0", s.MaxMagic)
	}
	if s.Score != 0 {
		t.Errorf("Score = %d, want 0", s.Score)
	}
	if s.Deaf {
		t.Error("Deaf should be false")
	}
	if s.Dumb {
		t.Error("Dumb should be false")
	}
	if s.Blind {
		t.Error("Blind should be false")
	}
	if s.Crippled {
		t.Error("Crippled should be false")
	}
	if s.ResetMinutes != 0 {
		t.Errorf("ResetMinutes = %d, want 0", s.ResetMinutes)
	}
	if s.Weather != 0 {
		t.Errorf("Weather = %d, want 0", s.Weather)
	}
	if s.DreamWord != "" {
		t.Errorf("DreamWord = %q, want empty", s.DreamWord)
	}
	if s.Rank != "" {
		t.Errorf("Rank = %q, want empty", s.Rank)
	}
	if s.StaminaColor != 0 {
		t.Errorf("StaminaColor = %d, want 0", s.StaminaColor)
	}
}

func TestStats_FieldAccess(t *testing.T) {
	s := Stats{
		Stamina:      10,
		MaxStamina:   20,
		Strength:     5,
		MaxStrength:  15,
		Dexterity:    8,
		MaxDexterity: 12,
		Magic:        3,
		MaxMagic:     30,
		Score:        99999,
		Deaf:         true,
		Dumb:         false,
		Blind:        true,
		Crippled:     false,
		ResetMinutes: 15,
		Weather:      3,
		DreamWord:    "quux",
		Rank:         "Wizard",
		StaminaColor: 2,
	}

	if s.Stamina != 10 {
		t.Errorf("Stamina = %d, want 10", s.Stamina)
	}
	if s.MaxStamina != 20 {
		t.Errorf("MaxStamina = %d, want 20", s.MaxStamina)
	}
	if s.Strength != 5 {
		t.Errorf("Strength = %d, want 5", s.Strength)
	}
	if s.MaxStrength != 15 {
		t.Errorf("MaxStrength = %d, want 15", s.MaxStrength)
	}
	if s.Dexterity != 8 {
		t.Errorf("Dexterity = %d, want 8", s.Dexterity)
	}
	if s.MaxDexterity != 12 {
		t.Errorf("MaxDexterity = %d, want 12", s.MaxDexterity)
	}
	if s.Magic != 3 {
		t.Errorf("Magic = %d, want 3", s.Magic)
	}
	if s.MaxMagic != 30 {
		t.Errorf("MaxMagic = %d, want 30", s.MaxMagic)
	}
	if s.Score != 99999 {
		t.Errorf("Score = %d, want 99999", s.Score)
	}
	if !s.Deaf {
		t.Error("Deaf should be true")
	}
	if s.Dumb {
		t.Error("Dumb should be false")
	}
	if !s.Blind {
		t.Error("Blind should be true")
	}
	if s.Crippled {
		t.Error("Crippled should be false")
	}
	if s.ResetMinutes != 15 {
		t.Errorf("ResetMinutes = %d, want 15", s.ResetMinutes)
	}
	if s.Weather != 3 {
		t.Errorf("Weather = %d, want 3", s.Weather)
	}
	if s.DreamWord != "quux" {
		t.Errorf("DreamWord = %q, want quux", s.DreamWord)
	}
	if s.Rank != "Wizard" {
		t.Errorf("Rank = %q, want Wizard", s.Rank)
	}
	if s.StaminaColor != 2 {
		t.Errorf("StaminaColor = %d, want 2", s.StaminaColor)
	}
}
