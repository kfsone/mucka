package network

import (
	"bytes"
	"testing"
)

func TestExtractDreamWord_NoMarkers(t *testing.T) {
	raw := []byte("hello world")
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false for plain text")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw slice returned, got %v", got)
	}
	// Verify same slice (no allocation)
	if &got[0] != &raw[0] {
		t.Error("expected same slice pointer (no allocation) when changed=false")
	}
}

func TestExtractDreamWord_SetOnly(t *testing.T) {
	// SET marker: \xAA\x9B\x9B followed by word
	raw := []byte{0xAA, 0x9B, 0x9B, 'f', 'r', 'o', 'g'}
	got, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "frog" {
		t.Errorf("expected word=%q, got %q", "frog", word)
	}
	want := "\x1B[36mfrog\x1B[0m"
	if string(got) != want {
		t.Errorf("expected %q, got %q", want, string(got))
	}
}

func TestExtractDreamWord_ClearOnly(t *testing.T) {
	// CLEAR marker: \xAA\x9B\x9C
	raw := []byte{0xAA, 0x9B, 0x9C}
	got, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if len(got) != 0 {
		t.Errorf("expected empty output, got %v", got)
	}
}

func TestExtractDreamWord_SetThenClear(t *testing.T) {
	// SET "foo" then CLEAR → last wins: empty
	raw := []byte{0xAA, 0x9B, 0x9B, 'f', 'o', 'o', 0xAA, 0x9B, 0x9C}
	_, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "" {
		t.Errorf("expected empty word after clear, got %q", word)
	}
}

func TestExtractDreamWord_ClearThenSet(t *testing.T) {
	// CLEAR then SET "foo" → last wins: "foo"
	raw := []byte{0xAA, 0x9B, 0x9C, 0xAA, 0x9B, 0x9B, 'f', 'o', 'o'}
	_, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "foo" {
		t.Errorf("expected word=%q, got %q", "foo", word)
	}
}

func TestExtractDreamWord_SetNoFollowingLetters(t *testing.T) {
	// \xAA\x9B\x9B with no letters after — no word extracted, treated as raw bytes
	raw := []byte{0xAA, 0x9B, 0x9B, '!'}
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false when SET has no letters")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractDreamWord_SetAtEndOfBuffer(t *testing.T) {
	// Partial marker at end — i+2 < len(raw) check fails, treated as raw
	raw := []byte{0xAA, 0x9B} // only 2 bytes, need i+2 < len for check
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false for partial marker at end")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractDreamWord_MultipleSetMarkers(t *testing.T) {
	// Multiple SET markers — last word wins
	raw := []byte{
		0xAA, 0x9B, 0x9B, 'a', 'b', 'c',
		0xAA, 0x9B, 0x9B, 'x', 'y', 'z',
	}
	_, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "xyz" {
		t.Errorf("expected last word %q, got %q", "xyz", word)
	}
}

func TestExtractDreamWord_WrongSequence(t *testing.T) {
	// 0xAA present but wrong pattern (second byte not 0x9B) → treated as raw
	raw := []byte{0xAA, 0x01, 0x9B, 0xFF, 'f', 'o', 'o'}
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false for wrong sequence")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractDreamWord_14CharWord(t *testing.T) {
	// Maximum 14-char word
	longWord := []byte("abcdefghijklmn") // 14 chars
	raw := append([]byte{0xAA, 0x9B, 0x9B}, longWord...)
	_, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "abcdefghijklmn" {
		t.Errorf("expected 14-char word, got %q", word)
	}
}

func TestExtractDreamWord_WordWithSurroundingText(t *testing.T) {
	// Text before and after the marker should be preserved
	prefix := []byte("before ")
	marker := []byte{0xAA, 0x9B, 0x9B, 'w', 'o', 'r', 'd'}
	suffix := []byte(" after")
	raw := append(append(prefix, marker...), suffix...)
	got, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "word" {
		t.Errorf("expected word=%q, got %q", "word", word)
	}
	want := "before \x1B[36mword\x1B[0m after"
	if string(got) != want {
		t.Errorf("expected %q, got %q", want, string(got))
	}
}

func TestExtractDreamWord_NoAA(t *testing.T) {
	// Fast path: no 0xAA byte at all
	raw := []byte{0x9B, 0x9C, 0xFF, 'h', 'i'}
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false when no 0xAA")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if &got[0] != &raw[0] {
		t.Error("expected same slice pointer for fast path")
	}
}

func TestExtractDreamWord_SetExactHeader_NoWord(t *testing.T) {
	// Complete 3-byte SET header with absolutely nothing after it.
	// Should be consumed silently: changed=false, raw returned.
	raw := []byte{0xAA, 0x9B, 0x9B}
	got, word, changed := extractDreamWord(raw)
	if changed {
		t.Error("expected changed=false for 4-byte SET header with no letters")
	}
	if word != "" {
		t.Errorf("expected empty word, got %q", word)
	}
	if !bytes.Equal(got, raw) {
		t.Errorf("expected raw returned, got %v", got)
	}
}

func TestExtractDreamWord_MalformedSetThenClear(t *testing.T) {
	// Malformed SET (no following letters) then CLEAR.
	// The 3 protocol bytes must NOT leak into the output.
	raw := []byte{0xAA, 0x9B, 0x9B, '!', 0xAA, 0x9B, 0x9C}
	got, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true (CLEAR was found)")
	}
	if word != "" {
		t.Errorf("expected empty word after CLEAR, got %q", word)
	}
	// Only the '!' between the two markers should appear; no protocol bytes.
	want := "!"
	if string(got) != want {
		t.Errorf("expected output %q, got %q", want, string(got))
	}
}

func TestExtractDreamWord_MalformedSetThenSet(t *testing.T) {
	// Malformed SET (no letters) then valid SET: protocol bytes must not appear in output.
	raw := []byte{0xAA, 0x9B, 0x9B, '!', 0xAA, 0x9B, 0x9B, 'c', 'a', 't'}
	got, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "cat" {
		t.Errorf("expected word=%q, got %q", "cat", word)
	}
	want := "!\x1B[36mcat\x1B[0m"
	if string(got) != want {
		t.Errorf("expected %q, got %q", want, string(got))
	}
}

func TestExtractDreamWord_WordOver14Chars(t *testing.T) {
	// The spec says 1–14 chars; the implementation is lenient and accepts more.
	// This test documents the current (permissive) behaviour.
	longWord := []byte("abcdefghijklmno") // 15 chars
	raw := append([]byte{0xAA, 0x9B, 0x9B}, longWord...)
	_, word, changed := extractDreamWord(raw)
	if !changed {
		t.Error("expected changed=true")
	}
	if word != "abcdefghijklmno" {
		t.Errorf("got %q", word)
	}
}
