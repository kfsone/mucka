package network

import "bytes"

// extractWeather scans raw (post-IAC-processed lineBuf bytes, WITHOUT trailing \n)
// for the MUD2 weather ctrl-d (0x04) protocol byte.
// Returns:
//   - processed: a new []byte with protocol markers stripped
//   - weatherCode: the weather code byte seen (0 = no change found)
//   - changed: true if any ctrl-d weather marker was found
//
// When changed is false, returns raw unmodified (no allocation).
// Multiple markers on the same line: last one wins.
func extractWeather(raw []byte) (processed []byte, weatherCode byte, changed bool) {
	if bytes.IndexByte(raw, 0x04) < 0 {
		return raw, 0, false
	}
	out := make([]byte, 0, len(raw))
	i := 0
	for i < len(raw) {
		b := raw[i]
		if b == 0x04 && i+1 < len(raw) {
			weatherCode = raw[i+1]
			changed = true
			i += 2
			continue
		}
		out = append(out, b)
		i++
	}
	// Guard: bytes.IndexByte guarantees at least one 0x04 exists, but if it is
	// the last byte (no following code byte), the loop condition i+1 < len(raw)
	// prevents changed from being set. Return the original slice in that case.
	if !changed {
		return raw, 0, false
	}
	return out, weatherCode, true
}
