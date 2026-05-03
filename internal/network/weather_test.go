package network

import (
	"bytes"
	"testing"
)

func TestExtractWeather_NoMarkers(t *testing.T) {
	raw := []byte("hello world")
	got, code, changed := extractWeather(raw)
	if changed {
		t.Error("expected changed=false for plain text")
	}
	if code != 0 {
		t.Errorf("expected code=0, got %d", code)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw slice returned, got %v", got)
	}
	// Verify same slice (no allocation)
	if &got[0] != &raw[0] {
		t.Error("expected same slice pointer (no allocation) when changed=false")
	}
}

func TestExtractWeather_SingleCode(t *testing.T) {
	// ctrl-d + 'F' embedded in text: ctrl-d pair should be stripped.
	raw := []byte{0x04, 'F', 'h', 'e', 'l', 'l', 'o'}
	got, code, changed := extractWeather(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if code != 'F' {
		t.Errorf("expected code='F' (%d), got %d", byte('F'), code)
	}
	want := "hello"
	if string(got) != want {
		t.Errorf("expected %q, got %q", want, string(got))
	}
}

func TestExtractWeather_MultipleMarkers_LastWins(t *testing.T) {
	// Multiple ctrl-d sequences — last one wins.
	raw := []byte{0x04, 'F', ' ', 0x04, 'R', ' ', 0x04, 'S'}
	_, code, changed := extractWeather(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if code != 'S' {
		t.Errorf("expected last code 'S', got %c (%d)", code, code)
	}
}

func TestExtractWeather_AtEndOfBuffer(t *testing.T) {
	// 0x04 at end with no following byte — i+1 < len(raw) check fails; treated as raw.
	raw := []byte{'h', 'i', 0x04}
	got, code, changed := extractWeather(raw)
	if changed {
		t.Error("expected changed=false for lone 0x04 at end")
	}
	if code != 0 {
		t.Errorf("expected code=0, got %d", code)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractWeather_CodeOnly(t *testing.T) {
	// Just ctrl-d + code, nothing else.
	raw := []byte{0x04, 'R'}
	got, code, changed := extractWeather(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if code != 'R' {
		t.Errorf("expected code='R', got %c (%d)", code, code)
	}
	if len(got) != 0 {
		t.Errorf("expected empty output, got %v", got)
	}
}

func TestExtractWeather_EmbeddedInText(t *testing.T) {
	// Text before and after the marker should be preserved.
	prefix := []byte("before ")
	marker := []byte{0x04, 'C'}
	suffix := []byte(" after")
	raw := append(append(prefix, marker...), suffix...)
	got, code, changed := extractWeather(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if code != 'C' {
		t.Errorf("expected code='C', got %c (%d)", code, code)
	}
	want := "before  after"
	if string(got) != want {
		t.Errorf("expected %q, got %q", want, string(got))
	}
}

func TestExtractWeather_Lone0x04(t *testing.T) {
	// Single 0x04 byte with nothing after — treated as raw.
	raw := []byte{0x04}
	got, code, changed := extractWeather(raw)
	if changed {
		t.Error("expected changed=false for lone 0x04")
	}
	if code != 0 {
		t.Errorf("expected code=0, got %d", code)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractWeather_No0x04(t *testing.T) {
	// Fast path: no 0x04 byte at all.
	raw := []byte{0x9B, 0xAA, 0xFF, 'h', 'i'}
	got, code, changed := extractWeather(raw)
	if changed {
		t.Error("expected changed=false when no 0x04")
	}
	if code != 0 {
		t.Errorf("expected code=0, got %d", code)
	}
	if &got[0] != &raw[0] {
		t.Error("expected same slice pointer for fast path")
	}
}

func TestExtractWeather_AllWeatherCodes(t *testing.T) {
	// Verify each recognized weather letter is passed through unchanged.
	codes := []byte{'F', 'C', 'R', 'S', 'O', 'T', 'B'}
	for _, c := range codes {
		raw := []byte{0x04, c}
		_, code, changed := extractWeather(raw)
		if !changed {
			t.Errorf("code '%c': expected changed=true", c)
		}
		if code != c {
			t.Errorf("code '%c': got %c", c, code)
		}
	}
}
