package headless

import (
	"bytes"
	"strings"
	"sync"
	"testing"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/mud2"
)

func TestPlainEmitter_AppendText_StripsANSI(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.AppendText("\x1b[31mred\x1b[0m text")

	got := strings.TrimRight(buf.String(), "\n")
	if got != "red text" {
		t.Errorf("AppendText = %q, want %q", got, "red text")
	}
}

func TestPlainEmitter_AppendText_Plain(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.AppendText("hello world")

	got := strings.TrimRight(buf.String(), "\n")
	if got != "hello world" {
		t.Errorf("AppendText = %q, want %q", got, "hello world")
	}
}

func TestPlainEmitter_AppendSpans(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	spans := []ansi.Span{{Text: "foo"}, {Text: "bar"}}
	p.AppendSpans(spans)

	got := strings.TrimRight(buf.String(), "\n")
	if got != "foobar" {
		t.Errorf("AppendSpans = %q, want %q", got, "foobar")
	}
}

func TestPlainEmitter_AppendSpans_SemanticTag(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)

	// Build a color map: cyan FG on default BG → type 6 (OBJECT).
	cm := mud2.NewColorMap()
	cm.ParseALLine("/ASCn6")
	p.SetColorMap(cm)

	cyanFG := ansi.StandardColor(6) // SGR 36 cyan
	spans := []ansi.Span{{Text: "a sword", FG: cyanFG, BG: ansi.DefaultBG}}
	p.AppendSpans(spans)

	got := strings.TrimRight(buf.String(), "\n")
	want := "[OBJECT] a sword"
	if got != want {
		t.Errorf("AppendSpans with color map = %q, want %q", got, want)
	}
}

func TestPlainEmitter_AppendSpans_NoTagForUnmappedColor(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)

	// Color map present but the span color is not in it.
	cm := mud2.NewColorMap()
	p.SetColorMap(cm)

	spans := []ansi.Span{{Text: "hello", FG: ansi.StandardColor(1), BG: ansi.DefaultBG}} // red
	p.AppendSpans(spans)

	got := strings.TrimRight(buf.String(), "\n")
	if got != "hello" {
		t.Errorf("AppendSpans with empty map = %q, want %q", got, "hello")
	}
}

func TestPlainEmitter_AppendText_SemanticTag(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)

	// ANSI SGR 33 = yellow (standard yellow FG on default BG) → type 13 (SAY).
	cm := mud2.NewColorMap()
	cm.ParseALLine("/ASYn13")
	p.SetColorMap(cm)

	// \x1b[33m applies SGR 33 (standard yellow).
	p.AppendText("\x1b[33mhello there\x1b[0m")

	got := strings.TrimRight(buf.String(), "\n")
	want := "[SAY] hello there"
	if got != want {
		t.Errorf("AppendText with color map = %q, want %q", got, want)
	}
}

func TestPlainEmitter_SetColorMap_NilDisablesTags(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)

	cm := mud2.NewColorMap()
	cm.ParseALLine("/ASCn6")
	p.SetColorMap(cm)
	p.SetColorMap(nil) // disable

	cyanFG := ansi.StandardColor(6)
	spans := []ansi.Span{{Text: "a sword", FG: cyanFG, BG: ansi.DefaultBG}}
	p.AppendSpans(spans)

	got := strings.TrimRight(buf.String(), "\n")
	if got != "a sword" {
		t.Errorf("AppendSpans after SetColorMap(nil) = %q, want %q", got, "a sword")
	}
}

func TestPlainEmitter_UpdatePartial_EmitsPrompt(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	spans := ansi.Parse("Enter name: ")
	p.UpdatePartial(spans)

	got := buf.String()
	want := "[PROMPT] Enter name: \n\n"
	if got != want {
		t.Errorf("UpdatePartial = %q, want %q", got, want)
	}
}

func TestPlainEmitter_UpdatePartial_Deduplicates(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	spans := ansi.Parse("Password: ")

	p.UpdatePartial(spans)
	p.UpdatePartial(spans) // same text — should not emit again
	p.UpdatePartial(spans)

	lines := strings.Count(buf.String(), "[PROMPT]")
	if lines != 1 {
		t.Errorf("expected 1 [PROMPT] line, got %d\noutput: %q", lines, buf.String())
	}
}

func TestPlainEmitter_UpdatePartial_EmitsDifferent(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)

	p.UpdatePartial(ansi.Parse("First: "))
	p.UpdatePartial(ansi.Parse("Second: "))

	lines := strings.Count(buf.String(), "[PROMPT]")
	if lines != 2 {
		t.Errorf("expected 2 [PROMPT] lines, got %d\noutput: %q", lines, buf.String())
	}
}

func TestPlainEmitter_EmitStats(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	st := &fes.Stats{
		Stamina: 10, MaxStamina: 20,
		Strength: 3, MaxStrength: 6,
		Dexterity: 4, MaxDexterity: 8,
		Magic: 5, MaxMagic: 10,
		Score:   1234567,
		Rank:    "hero",
		Level:   7,
		Weather: 2,
	}
	p.EmitStats(st)

	got := strings.TrimRight(buf.String(), "\n")
	checks := []string{
		"[STATUS]",
		"sta=10/20",
		"str=3/6",
		"dex=4/8",
		"mag=5/10",
		"score=1,234,567",
		"rank=hero",
		"level=7",
		"weather=2",
	}
	for _, want := range checks {
		if !strings.Contains(got, want) {
			t.Errorf("EmitStats output %q missing %q", got, want)
		}
	}
}

func TestPlainEmitter_EmitDreamWord_Set(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.EmitDreamWord("banana")

	got := strings.TrimRight(buf.String(), "\n")
	want := "[DREAMWORD] banana"
	if got != want {
		t.Errorf("EmitDreamWord = %q, want %q", got, want)
	}
}

func TestPlainEmitter_EmitDreamWord_Cleared(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.EmitDreamWord("")

	got := strings.TrimRight(buf.String(), "\n")
	want := "[DREAMWORD] (none)"
	if got != want {
		t.Errorf("EmitDreamWord(empty) = %q, want %q", got, want)
	}
}

func TestPlainEmitter_EmitSent(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.EmitSent("wave")

	got := strings.TrimRight(buf.String(), "\n")
	want := "[SENT] wave"
	if got != want {
		t.Errorf("EmitSent = %q, want %q", got, want)
	}
}

func TestPlainEmitter_EmitError(t *testing.T) {
	var buf bytes.Buffer
	p := NewPlainEmitter(&buf)
	p.EmitError("not connected: hello")

	got := strings.TrimRight(buf.String(), "\n")
	want := "[ERROR] not connected: hello"
	if got != want {
		t.Errorf("EmitError = %q, want %q", got, want)
	}
}

func TestPlainEmitter_GoroutineSafety(t *testing.T) {
	var buf safeBuffer
	p := NewPlainEmitter(&buf)

	const goroutines = 20
	const perGoroutine = 50
	var wg sync.WaitGroup
	wg.Add(goroutines)
	for i := 0; i < goroutines; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < perGoroutine; j++ {
				p.EmitSent("wave")
			}
		}()
	}
	wg.Wait()

	data := buf.Bytes()
	lines := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
	if len(lines) != goroutines*perGoroutine {
		t.Fatalf("expected %d lines, got %d", goroutines*perGoroutine, len(lines))
	}
	for i, line := range lines {
		if !strings.HasPrefix(line, "[SENT] ") {
			t.Errorf("line %d has unexpected format: %q", i, line)
		}
	}
}
