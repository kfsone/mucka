package fes

import (
	"bytes"
	"strconv"
	"strings"
)

// TriggerBytes is the byte sequence the client sends to request a FES packet.
// ESC - [ F E S ESC - ]
var TriggerBytes = []byte{0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D}

// PacketPrefix is the text prefix that starts a FES response line.
// mudii.co.uk sends "**" followed immediately by space-separated fields.
var PacketPrefix = []byte("**")

// ParsePacket parses a FES response packet body (after stripping the PacketPrefix).
// Returns true and updates s if the packet is valid, false if malformed.
// Body is space-delimited: sta msta str mstr dex mdex mag mmag score blind deaf crippled dumb reset weather
// An optional 0xFE stamina-color marker may precede the fields; if present the
// byte immediately following it is stored as StaminaColor.
func ParsePacket(data []byte, s *Stats) bool {
	// Optional binary stamina-colour marker from older server variants.
	if feIdx := bytes.IndexByte(data, 0xFE); feIdx >= 0 && feIdx+1 < len(data) {
		s.StaminaColor = int(data[feIdx+1])
		data = data[feIdx+2:]
	}

	fields := strings.Fields(string(data))
	if len(fields) < 15 {
		return false
	}

	parseInt := func(str string) (int, bool) {
		n, err := strconv.Atoi(str)
		return n, err == nil
	}

	// parseBool accepts "Y"/"N" (case-insensitive) or "0"/"1" integers.
	parseBool := func(str string) (bool, bool) {
		switch strings.ToUpper(str) {
		case "Y":
			return true, true
		case "N":
			return false, true
		}
		n, err := strconv.Atoi(str)
		return n != 0, err == nil
	}

	var ok bool
	if s.Stamina, ok = parseInt(fields[0]); !ok {
		return false
	}
	if s.MaxStamina, ok = parseInt(fields[1]); !ok {
		return false
	}
	if s.Strength, ok = parseInt(fields[2]); !ok {
		return false
	}
	if s.MaxStrength, ok = parseInt(fields[3]); !ok {
		return false
	}
	if s.Dexterity, ok = parseInt(fields[4]); !ok {
		return false
	}
	if s.MaxDexterity, ok = parseInt(fields[5]); !ok {
		return false
	}
	if s.Magic, ok = parseInt(fields[6]); !ok {
		return false
	}
	if s.MaxMagic, ok = parseInt(fields[7]); !ok {
		return false
	}

	score, err := strconv.ParseInt(fields[8], 10, 64)
	if err != nil {
		return false
	}
	s.Score = score

	var reset int
	if s.Blind, ok = parseBool(fields[9]); !ok {
		return false
	}
	if s.Deaf, ok = parseBool(fields[10]); !ok {
		return false
	}
	if s.Crippled, ok = parseBool(fields[11]); !ok {
		return false
	}
	if s.Dumb, ok = parseBool(fields[12]); !ok {
		return false
	}
	if reset, ok = parseInt(fields[13]); !ok {
		return false
	}
	s.ResetMinutes = reset

	// Weather: try integer first, fall back to raw byte value of first character.
	if n, err := strconv.Atoi(fields[14]); err == nil {
		s.Weather = byte(n)
	} else if len(fields[14]) > 0 {
		s.Weather = fields[14][0]
	} else {
		return false
	}

	return true
}
