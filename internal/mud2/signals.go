// Package mud2 provides detection of game-state signals embedded in MUD2
// server output. MUD2 communicates control events and state changes as
// specific strings inside the plain text and raw byte stream; centralising
// those patterns here keeps the rest of the code free of magic constants.
package mud2

import (
	"bytes"
	"strings"
)

// GameEntrySignal is the substring the MUD2 server sends when the player
// first enters the game world. "Elizabethan" is the opening-area name in
// MUD2 ("Elizabethan London"). Detecting this string in server output
// confirms that the player has successfully entered the game world and that
// FES (stats) polling should begin. If this string never appears, FES
// polling never starts and the stats bar remains empty for the session.
const GameEntrySignal = "Elizabethan"

// ClientMenuPrompt is the plain-text prompt MUD2 displays when the player
// is at the main client menu (i.e. outside the game world).
const ClientMenuPrompt = "Option: "

// clientModeEscapes are the ESC-hyphen-X byte sequences MUD2 sends to
// signal "return to client menu". These match the patterns in Clio's
// telnet.l (ESC-C, ESC-R, ESC-r, ESC-K).
var clientModeEscapes = [][]byte{
	{0x1b, '-', 'C'},
	{0x1b, '-', 'R'},
	{0x1b, '-', 'r'},
	{0x1b, '-', 'K'},
}

// IsGameEntry reports whether plainText contains the game-entry signal,
// indicating the player has just entered the MUD2 game world.
// See GameEntrySignal for the full rationale.
func IsGameEntry(plainText string) bool {
	return strings.Contains(plainText, GameEntrySignal)
}

// IsClientModeSignal reports whether the raw line bytes or plain text
// indicate that MUD2 has returned to the client menu (exiting the game
// world). Both the binary escape sequences and the "Option: " text prompt
// are checked, mirroring Clio's detection logic.
func IsClientModeSignal(raw []byte, plain string) bool {
	for _, seq := range clientModeEscapes {
		if bytes.Contains(raw, seq) {
			return true
		}
	}
	return strings.Contains(plain, ClientMenuPrompt)
}
