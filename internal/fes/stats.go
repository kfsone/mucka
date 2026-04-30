// Package fes implements the MUD2 FES (Fast Exchange Stats) protocol and
// plain-text stat scanner.
package fes

// Stats holds the most recently extracted MUD2 character statistics.
type Stats struct {
	Stamina, MaxStamina     int
	Strength, MaxStrength   int
	Dexterity, MaxDexterity int
	Magic, MaxMagic         int
	Score                   int64
	Deaf, Dumb, Blind, Crippled bool
	ResetMinutes            int
	Weather                 byte
	DreamWord               string
	Level                   int    // numeric level (e.g. 5)
	Rank                    string // rank name (e.g. "hero")
	StaminaColor            int
}
