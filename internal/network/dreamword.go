package network

import "bytes"

// extractDreamWord scans raw (post-IAC-processed lineBuf bytes, WITHOUT trailing \n) for
// MUD2 dream-word protocol bytes.
// Returns:
//   - processed: a new []byte with protocol markers replaced by ANSI cyan/reset sequences
//   - finalWord: the last dream word seen ("" means cleared/none)
//   - changed: true if any marker was found
//
// When changed is false, returns raw unmodified (no allocation).
// Multiple markers on the same line: last one wins.
func extractDreamWord(raw []byte) (processed []byte, finalWord string, changed bool) {
	if bytes.IndexByte(raw, 0xAA) < 0 {
		return raw, "", false
	}
	out := make([]byte, 0, len(raw)+32)
	i := 0
	for i < len(raw) {
		b := raw[i]
		if b == 0xAA && i+2 < len(raw) && raw[i+1] == 0x9B {
			switch raw[i+2] {
			case 0x9B: // SET: find 1-14 lowercase letters
				j := i + 3
				for j < len(raw) && raw[j] >= 'a' && raw[j] <= 'z' {
					j++
				}
				word := string(raw[i+3 : j])
				if word != "" {
					finalWord = word
					changed = true
					out = append(out, "\x1B[36m"...)
					out = append(out, raw[i+3:j]...)
					out = append(out, "\x1B[0m"...)
					i = j
					continue
				}
				// SET with no letters: consume the 3-byte header silently so protocol
				// bytes never appear in output (even when changed=true due to a later marker).
				i += 3
				continue
			case 0x9C: // CLEAR
				finalWord = ""
				changed = true
				i += 3
				continue
			}
		}
		out = append(out, b)
		i++
	}
	if !changed {
		return raw, "", false
	}
	return out, finalWord, true
}
