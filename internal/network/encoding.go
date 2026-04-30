package network

// latin1ToUTF8 converts a Latin-1 (ISO-8859-1) byte slice to a UTF-8 string.
// Each byte value in latin-1 maps directly to the same Unicode code point, so
// bytes 0x00–0x7F are unchanged and bytes 0x80–0xFF become two-byte UTF-8
// sequences. This is correct for MUD2, which sends ISO-8859-1 encoded text.
func latin1ToUTF8(b []byte) string {
	// Fast path: if all bytes are ASCII there is nothing to do.
	ascii := true
	for _, c := range b {
		if c > 0x7F {
			ascii = false
			break
		}
	}
	if ascii {
		return string(b)
	}

	runes := make([]rune, len(b))
	for i, c := range b {
		runes[i] = rune(c)
	}
	return string(runes)
}
