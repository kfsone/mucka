package fes

import (
	"strconv"
	"strings"
)

// parseFirstInt extracts the first integer found in s, ignoring leading
// non-digit characters.  Returns 0, false if no integer is found.
// The parsed value is expected to fit within int's range (it represents small game stats).
func parseFirstInt(s string) (int, bool) {
	n, ok := parseFirstInt64(s)
	return int(n), ok
}

// parseFirstInt64 extracts the first int64 found in s.
func parseFirstInt64(s string) (int64, bool) {
	s = strings.TrimSpace(s)
	start := -1
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c >= '0' && c <= '9' {
			if start < 0 {
				start = i
			}
		} else if start >= 0 {
			n, err := strconv.ParseInt(s[start:i], 10, 64)
			return n, err == nil
		}
	}
	if start >= 0 {
		n, err := strconv.ParseInt(s[start:], 10, 64)
		return n, err == nil
	}
	return 0, false
}

// ScanLine inspects a plain-text (ANSI-stripped) line and updates stats in place.
// Returns true if any field was updated.
// Patterns matched (case-insensitive):
//
//	"stamina:"              → Stamina; also MaxStamina from "N/N", or "max: N" on same line
//	"strength:"             → MaxStrength; Strength=MaxStrength when no "effective strength:" present
//	"effective strength:"   → Strength              (current effective)
//	"dexterity:"            → MaxDexterity; Dexterity=MaxDexterity when no "effective dexterity:" present
//	"effective dexterity:"  → Dexterity
//	"score:"                → Score (commas stripped)
//	"level:"                → Level (integer) and Rank (next token), or just Rank for old format
//	"Your stamina is "      → Stamina
//	"(Persona saved on "    → Score (first number after "score" in line, commas stripped)
func ScanLine(line string, s *Stats) bool {
	lower := strings.ToLower(line)
	updated := false

	// "effective strength:" → Strength (current effective value).
	effectiveStrengthIdx := strings.Index(lower, "effective strength:")
	if effectiveStrengthIdx >= 0 {
		if n, ok := parseFirstInt(line[effectiveStrengthIdx+len("effective strength:"):]); ok {
			s.Strength = n
			updated = true
		}
	}

	// "effective dexterity:" → Dexterity (current effective value).
	effectiveDexterityIdx := strings.Index(lower, "effective dexterity:")
	if effectiveDexterityIdx >= 0 {
		if n, ok := parseFirstInt(line[effectiveDexterityIdx+len("effective dexterity:"):]); ok {
			s.Dexterity = n
			updated = true
		}
	}

	// "stamina:" → Stamina and optionally MaxStamina.
	// Supports "N/N", plain "N", and "N  max:  N" formats.
	if idx := strings.Index(lower, "stamina:"); idx >= 0 {
		rest := strings.TrimSpace(line[idx+len("stamina:"):])
		if slash := strings.IndexByte(rest, '/'); slash >= 0 {
			if n, ok := parseFirstInt(rest[:slash]); ok {
				s.Stamina = n
				updated = true
			}
			if n, ok := parseFirstInt(rest[slash+1:]); ok {
				s.MaxStamina = n
				updated = true
			}
		} else if n, ok := parseFirstInt(rest); ok {
			s.Stamina = n
			updated = true
			// Also check for "max:" on the same line (e.g. "stamina:  30  max:  100").
			if maxIdx := strings.Index(lower[idx:], "max:"); maxIdx >= 0 {
				absMaxIdx := idx + maxIdx
				if n2, ok := parseFirstInt(line[absMaxIdx+len("max:"):]); ok {
					s.MaxStamina = n2
					updated = true
				}
			}
		}
	}

	// "strength:" → MaxStrength, but only when NOT part of "effective strength:".
	// Scan all occurrences so a line can contain both patterns.
	maxStrengthUpdated := false
	for search, off := lower, 0; ; {
		idx := strings.Index(search, "strength:")
		if idx < 0 {
			break
		}
		absIdx := off + idx
		effLen := len("effective ")
		isEffective := absIdx >= effLen && lower[absIdx-effLen:absIdx] == "effective "
		if !isEffective {
			if n, ok := parseFirstInt(line[absIdx+len("strength:"):]); ok {
				s.MaxStrength = n
				updated = true
				maxStrengthUpdated = true
			}
		}
		off += idx + len("strength:")
		search = lower[off:]
	}
	// When MaxStrength was updated but no "effective strength:" on this line,
	// treat Strength as equal to MaxStrength (full base stat, no debuff).
	if maxStrengthUpdated && effectiveStrengthIdx < 0 {
		s.Strength = s.MaxStrength
	}

	// "dexterity:" → MaxDexterity, but only when NOT part of "effective dexterity:".
	maxDexterityUpdated := false
	for search, off := lower, 0; ; {
		idx := strings.Index(search, "dexterity:")
		if idx < 0 {
			break
		}
		absIdx := off + idx
		effLen := len("effective ")
		isEffective := absIdx >= effLen && lower[absIdx-effLen:absIdx] == "effective "
		if !isEffective {
			if n, ok := parseFirstInt(line[absIdx+len("dexterity:"):]); ok {
				s.MaxDexterity = n
				updated = true
				maxDexterityUpdated = true
			}
		}
		off += idx + len("dexterity:")
		search = lower[off:]
	}
	// When MaxDexterity was updated but no "effective dexterity:" on this line,
	// treat Dexterity as equal to MaxDexterity.
	if maxDexterityUpdated && effectiveDexterityIdx < 0 {
		s.Dexterity = s.MaxDexterity
	}

	// "score:" → Score (strip commas to handle "5,184 points" format).
	if idx := strings.Index(lower, "score:"); idx >= 0 {
		noCommas := strings.ReplaceAll(line[idx+len("score:"):], ",", "")
		if n, ok := parseFirstInt64(noCommas); ok {
			s.Score = n
			updated = true
		}
	}

	// "level:" → Level (integer) then Rank (next token), or just Rank for old format.
	// New: "level:  5  hero" → Level=5, Rank="hero"
	// Old: "level:  Warlock" → Rank="Warlock"
	if idx := strings.Index(lower, "level:"); idx >= 0 {
		rest := strings.TrimSpace(line[idx+len("level:"):])
		if fields := strings.Fields(rest); len(fields) > 0 {
			if n, err := strconv.Atoi(fields[0]); err == nil {
				s.Level = n
				updated = true
				if len(fields) > 1 {
					s.Rank = fields[1]
					updated = true
				}
			} else {
				s.Rank = fields[0]
				updated = true
			}
		}
	}

	// "Your stamina is N" → Stamina.
	if idx := strings.Index(lower, "your stamina is "); idx >= 0 {
		if n, ok := parseFirstInt(line[idx+len("your stamina is "):]); ok {
			s.Stamina = n
			updated = true
		}
	}

	// "(Persona saved on " → Score (first number after "score" in the line, commas stripped).
	if strings.Contains(lower, "(persona saved on ") {
		if scoreIdx := strings.Index(lower, "score"); scoreIdx >= 0 {
			noCommas := strings.ReplaceAll(line[scoreIdx+len("score"):], ",", "")
			if n, ok := parseFirstInt64(noCommas); ok {
				s.Score = n
				updated = true
			}
		}
	}

	return updated
}
