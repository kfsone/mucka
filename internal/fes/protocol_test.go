package fes

import (
	"bytes"
	"testing"
)

// buildBody constructs a FES packet body with the given stamina color and
// space-separated field string.
func buildBody(color byte, fields string) []byte {
	var b bytes.Buffer
	b.WriteByte(0xFE)
	b.WriteByte(color)
	if fields != "" {
		b.WriteByte(' ')
		b.WriteString(fields)
	}
	return b.Bytes()
}

const validFields = "25 30 10 15 8 12 3 30 12345 0 0 0 0 15 3"

func TestParsePacket_Valid(t *testing.T) {
	data := buildBody(2, validFields)
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false for valid packet")
	}
	if s.StaminaColor != 2 {
		t.Errorf("StaminaColor = %d, want 2", s.StaminaColor)
	}
	if s.Stamina != 25 {
		t.Errorf("Stamina = %d, want 25", s.Stamina)
	}
	if s.MaxStamina != 30 {
		t.Errorf("MaxStamina = %d, want 30", s.MaxStamina)
	}
	if s.Strength != 10 {
		t.Errorf("Strength = %d, want 10", s.Strength)
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
	if s.Score != 12345 {
		t.Errorf("Score = %d, want 12345", s.Score)
	}
	if s.Blind {
		t.Error("Blind should be false")
	}
	if s.Deaf {
		t.Error("Deaf should be false")
	}
	if s.Crippled {
		t.Error("Crippled should be false")
	}
	if s.Dumb {
		t.Error("Dumb should be false")
	}
	if s.ResetMinutes != 15 {
		t.Errorf("ResetMinutes = %d, want 15", s.ResetMinutes)
	}
	if s.Weather != 3 {
		t.Errorf("Weather = %d, want 3", s.Weather)
	}
}

func TestParsePacket_BoolFlags(t *testing.T) {
	// blind=1 deaf=1 crippled=1 dumb=1
	data := buildBody(0, "1 2 3 4 5 6 7 8 99 1 1 1 1 0 0")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false")
	}
	if !s.Blind {
		t.Error("Blind should be true")
	}
	if !s.Deaf {
		t.Error("Deaf should be true")
	}
	if !s.Crippled {
		t.Error("Crippled should be true")
	}
	if !s.Dumb {
		t.Error("Dumb should be true")
	}
}

func TestParsePacket_Empty(t *testing.T) {
	var s Stats
	if ParsePacket(nil, &s) {
		t.Error("ParsePacket(nil) should return false")
	}
	if ParsePacket([]byte{}, &s) {
		t.Error("ParsePacket(empty) should return false")
	}
}

func TestParsePacket_TextFormat(t *testing.T) {
	// Plain text body (no 0xFE marker) — the mudii.co.uk "**" format after prefix stripping.
	var s Stats
	if !ParsePacket([]byte("25 30 10 15 8 12 3 30 12345 0 0 0 0 15 3"), &s) {
		t.Error("ParsePacket without 0xFE marker should succeed (0xFE is optional)")
	}
	if s.Stamina != 25 {
		t.Errorf("Stamina = %d, want 25", s.Stamina)
	}
	if s.Score != 12345 {
		t.Errorf("Score = %d, want 12345", s.Score)
	}
}

func TestParsePacket_FEAtEnd(t *testing.T) {
	var s Stats
	// 0xFE at last position: condition (feIdx+1 < len) fails, so FE is NOT consumed;
	// whole data is treated as fields, which are too few → false.
	if ParsePacket([]byte{0x01, 0x02, 0xFE}, &s) {
		t.Error("ParsePacket with 0xFE at end should return false")
	}
}

func TestParsePacket_StarStarRealFormat(t *testing.T) {
	// Actual format received from mudii.co.uk after stripping the "**" prefix.
	// Fields: sta msta str mstr dex mdex mag mmag score blind deaf crippled dumb reset weather
	data := []byte("100 100 100 100 100 100 0 100 7652 N N N N 23 R")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false for real ** format")
	}
	if s.Stamina != 100 || s.MaxStamina != 100 {
		t.Errorf("Stamina = %d/%d, want 100/100", s.Stamina, s.MaxStamina)
	}
	if s.Strength != 100 || s.MaxStrength != 100 {
		t.Errorf("Strength = %d/%d, want 100/100", s.Strength, s.MaxStrength)
	}
	if s.Magic != 0 || s.MaxMagic != 100 {
		t.Errorf("Magic = %d/%d, want 0/100", s.Magic, s.MaxMagic)
	}
	if s.Score != 7652 {
		t.Errorf("Score = %d, want 7652", s.Score)
	}
	if s.Blind || s.Deaf || s.Crippled || s.Dumb {
		t.Error("ailments should all be false")
	}
	if s.ResetMinutes != 23 {
		t.Errorf("ResetMinutes = %d, want 23", s.ResetMinutes)
	}
	if s.Weather != 'R' {
		t.Errorf("Weather = %d, want %d ('R')", s.Weather, 'R')
	}
}

func TestParsePacket_TooFewFields(t *testing.T) {
	// Only 14 fields instead of 15
	data := buildBody(1, "25 30 10 15 8 12 3 30 12345 0 0 0 0 15")
	var s Stats
	if ParsePacket(data, &s) {
		t.Error("ParsePacket with 14 fields should return false")
	}
}

func TestParsePacket_NonIntegerField(t *testing.T) {
	data := buildBody(1, "25 30 10 15 8 12 3 30 NOTANUMBER 0 0 0 0 15 3")
	var s Stats
	if ParsePacket(data, &s) {
		t.Error("ParsePacket with non-integer score should return false")
	}
}

func TestParsePacket_ExtraFields(t *testing.T) {
	// Extra fields beyond 15 should be ignored
	data := buildBody(5, validFields+" extra ignored")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket with extra fields should return true")
	}
	if s.Stamina != 25 {
		t.Errorf("Stamina = %d, want 25", s.Stamina)
	}
}

func TestParsePacket_PrefixData(t *testing.T) {
	// Data before 0xFE marker is ignored (e.g. rank/dreamword prefix)
	var buf bytes.Buffer
	buf.WriteString("SomeRankData")
	buf.Write(buildBody(3, validFields))
	var s Stats
	if !ParsePacket(buf.Bytes(), &s) {
		t.Fatal("ParsePacket with prefix data should return true")
	}
	if s.StaminaColor != 3 {
		t.Errorf("StaminaColor = %d, want 3", s.StaminaColor)
	}
}

func TestParsePacket_YNBooleans(t *testing.T) {
	// Real MUD2 FES format: Y/N for booleans, letter for weather.
	data := buildBody(1, "95 95 100 100 95 95 0 95 5170 N N N N 44 F")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false for Y/N boolean packet")
	}
	if s.Stamina != 95 {
		t.Errorf("Stamina = %d, want 95", s.Stamina)
	}
	if s.MaxStamina != 95 {
		t.Errorf("MaxStamina = %d, want 95", s.MaxStamina)
	}
	if s.Strength != 100 {
		t.Errorf("Strength = %d, want 100", s.Strength)
	}
	if s.MaxStrength != 100 {
		t.Errorf("MaxStrength = %d, want 100", s.MaxStrength)
	}
	if s.Dexterity != 95 {
		t.Errorf("Dexterity = %d, want 95", s.Dexterity)
	}
	if s.MaxDexterity != 95 {
		t.Errorf("MaxDexterity = %d, want 95", s.MaxDexterity)
	}
	if s.Magic != 0 {
		t.Errorf("Magic = %d, want 0", s.Magic)
	}
	if s.MaxMagic != 95 {
		t.Errorf("MaxMagic = %d, want 95", s.MaxMagic)
	}
	if s.Score != 5170 {
		t.Errorf("Score = %d, want 5170", s.Score)
	}
	if s.Blind {
		t.Error("Blind should be false (N)")
	}
	if s.Deaf {
		t.Error("Deaf should be false (N)")
	}
	if s.Crippled {
		t.Error("Crippled should be false (N)")
	}
	if s.Dumb {
		t.Error("Dumb should be false (N)")
	}
	if s.ResetMinutes != 44 {
		t.Errorf("ResetMinutes = %d, want 44", s.ResetMinutes)
	}
	// 'F' = 70 decimal
	if s.Weather != 'F' {
		t.Errorf("Weather = %d, want %d ('F')", s.Weather, byte('F'))
	}
}

func TestParsePacket_BoolNonOneInt(t *testing.T) {
	// "2" is a non-zero integer; parseBool should treat it as true.
	data := buildBody(0, "1 2 3 4 5 6 7 8 99 2 0 0 0 0 0")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false")
	}
	if !s.Blind {
		t.Error("Blind should be true for bool field '2'")
	}
}

func TestParsePacket_InvalidBoolField(t *testing.T) {
	// "X" is neither Y/N nor an integer; ParsePacket should return false.
	data := buildBody(0, "1 2 3 4 5 6 7 8 99 X 0 0 0 0 0")
	var s Stats
	if ParsePacket(data, &s) {
		t.Error("ParsePacket should return false for invalid bool field 'X'")
	}
}

func TestParsePacket_YNBooleans_True(t *testing.T) {
	// Y values should set booleans to true; lowercase y should also work.
	data := buildBody(2, "10 20 30 40 50 60 70 80 999 Y y Y y 5 S")
	var s Stats
	if !ParsePacket(data, &s) {
		t.Fatal("ParsePacket returned false for Y boolean packet")
	}
	if !s.Blind {
		t.Error("Blind should be true (Y)")
	}
	if !s.Deaf {
		t.Error("Deaf should be true (y)")
	}
	if !s.Crippled {
		t.Error("Crippled should be true (Y)")
	}
	if !s.Dumb {
		t.Error("Dumb should be true (y)")
	}
	// 'S' = 83 decimal
	if s.Weather != 'S' {
		t.Errorf("Weather = %d, want %d ('S')", s.Weather, byte('S'))
	}
}
