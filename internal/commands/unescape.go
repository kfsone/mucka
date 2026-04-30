package commands

import "strings"

// unescapeStreamLine interprets backslash escape sequences in s:
//
//	\x1b or \x1B  →  ESC byte (0x1B)
//	\e            →  ESC byte (0x1B)
//	\\            →  literal backslash
//	\n            →  newline
//
// All other backslash sequences are left unchanged.
func unescapeStreamLine(s string) string {
	if !strings.ContainsRune(s, '\\') {
		return s
	}
	var buf strings.Builder
	buf.Grow(len(s))
	i := 0
	for i < len(s) {
		if s[i] != '\\' || i+1 >= len(s) {
			buf.WriteByte(s[i])
			i++
			continue
		}
		switch s[i+1] {
		case '\\':
			buf.WriteByte('\\')
			i += 2
		case 'n':
			buf.WriteByte('\n')
			i += 2
		case 'e':
			buf.WriteByte(0x1b)
			i += 2
		case 'x', 'X':
			// \x1b or \x1B → ESC
			if i+3 < len(s) && (s[i+2] == '1') && (s[i+3] == 'b' || s[i+3] == 'B') {
				buf.WriteByte(0x1b)
				i += 4
			} else {
				buf.WriteByte(s[i])
				i++
			}
		default:
			buf.WriteByte(s[i])
			i++
		}
	}
	return buf.String()
}
