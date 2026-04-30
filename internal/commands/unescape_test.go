package commands

import "testing"

func TestUnescapeStreamLineNoEscapes(t *testing.T) {
	in := "hello world"
	if got := unescapeStreamLine(in); got != in {
		t.Errorf("got %q, want %q", got, in)
	}
}

func TestUnescapeStreamLineEscapeE(t *testing.T) {
	in := `\e[32mgreen\e[0m`
	want := "\x1b[32mgreen\x1b[0m"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineEscapeX1b(t *testing.T) {
	in := `\x1b[31mred\x1b[0m`
	want := "\x1b[31mred\x1b[0m"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineEscapeX1B(t *testing.T) {
	in := `\x1B[33myellow\x1B[0m`
	want := "\x1b[33myellow\x1b[0m"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineDoubleBackslash(t *testing.T) {
	in := `foo\\bar`
	want := `foo\bar`
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineNewline(t *testing.T) {
	in := `line1\nline2`
	want := "line1\nline2"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineUnknownEscape(t *testing.T) {
	// Unknown escape sequences pass through unchanged (backslash preserved).
	in := `\z`
	want := `\z`
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineTrailingBackslash(t *testing.T) {
	// A lone trailing backslash is emitted as-is.
	in := `abc\`
	want := `abc\`
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineMixed(t *testing.T) {
	in := `\e[1;32mBold Green\e[0m and \\backslash\\ done`
	want := "\x1b[1;32mBold Green\x1b[0m and \\backslash\\ done"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

func TestUnescapeStreamLineEmptyString(t *testing.T) {
	if got := unescapeStreamLine(""); got != "" {
		t.Errorf("got %q, want empty string", got)
	}
}

// TestUnescapeStreamLineConsecutiveEscapes verifies two adjacent escape sequences
// are both expanded correctly.
func TestUnescapeStreamLineConsecutiveEscapes(t *testing.T) {
	in := `\e\e`
	want := "\x1b\x1b"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// TestUnescapeStreamLineConsecutiveMixedEscapes verifies \e and \x1b adjacent.
func TestUnescapeStreamLineConsecutiveMixedEscapes(t *testing.T) {
	in := `\e\x1b`
	want := "\x1b\x1b"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}

// TestUnescapeStreamLineX1BUppercase verifies \X1B (uppercase X) also expands.
func TestUnescapeStreamLineX1BUppercaseX(t *testing.T) {
	in := `\X1B[0m`
	want := "\x1b[0m"
	if got := unescapeStreamLine(in); got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}
