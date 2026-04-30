package network

import "testing"

func TestLatin1ToUTF8_ASCII(t *testing.T) {
	in := []byte("Hello, world!")
	got := latin1ToUTF8(in)
	if got != "Hello, world!" {
		t.Errorf("ASCII round-trip failed: %q", got)
	}
}

func TestLatin1ToUTF8_HighBytes(t *testing.T) {
	// 0xE9 = é, 0xFC = ü, 0xE0 = à in latin-1 / Unicode
	in := []byte{0xE9, 0xFC, 0xE0}
	got := latin1ToUTF8(in)
	want := "éüà"
	if got != want {
		t.Errorf("latin1 high bytes: got %q, want %q", got, want)
	}
}

func TestLatin1ToUTF8_Mixed(t *testing.T) {
	// "caf\xe9" should become "café"
	in := []byte{'c', 'a', 'f', 0xE9}
	got := latin1ToUTF8(in)
	want := "café"
	if got != want {
		t.Errorf("mixed: got %q, want %q", got, want)
	}
}

func TestLatin1ToUTF8_Empty(t *testing.T) {
	got := latin1ToUTF8(nil)
	if got != "" {
		t.Errorf("empty: got %q", got)
	}
}
