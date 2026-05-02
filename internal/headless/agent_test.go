package headless

import (
	"bytes"
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/fes"
)

// decodeOne asserts that buf contains exactly one complete JSON object,
// decodes it, and returns the result map.
func decodeOne(t *testing.T, buf *bytes.Buffer) map[string]any {
	t.Helper()
	line, err := buf.ReadString('\n')
	if err != nil {
		t.Fatalf("no newline-terminated line in output: %v", err)
	}
	line = strings.TrimRight(line, "\n")
	var m map[string]any
	if err := json.Unmarshal([]byte(line), &m); err != nil {
		t.Fatalf("invalid JSON %q: %v", line, err)
	}
	return m
}

func TestAgentEmitter_TextFromString(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.AppendText("hello world")

	m := decodeOne(t, &buf)
	if m["event"] != "text" {
		t.Errorf("event = %q, want %q", m["event"], "text")
	}
	if m["text"] != "hello world" {
		t.Errorf("text = %q, want %q", m["text"], "hello world")
	}
	if _, ok := m["time"]; !ok {
		t.Error("missing time field")
	}
}

func TestAgentEmitter_TextStripsANSI(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.AppendText("\x1b[31mred\x1b[0m")

	m := decodeOne(t, &buf)
	if m["text"] != "red" {
		t.Errorf("ANSI not stripped: text = %q", m["text"])
	}
}

func TestAgentEmitter_TextFromSpans(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	spans := ansi.Parse("hello spans")
	a.AppendSpans(spans)

	m := decodeOne(t, &buf)
	if m["event"] != "text" {
		t.Errorf("event = %q, want %q", m["event"], "text")
	}
	if m["text"] != "hello spans" {
		t.Errorf("text = %q, want %q", m["text"], "hello spans")
	}
}

func TestAgentEmitter_Partial(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	spans := ansi.Parse("Enter password: ")
	a.UpdatePartial(spans)

	m := decodeOne(t, &buf)
	if m["event"] != "partial" {
		t.Errorf("event = %q, want %q", m["event"], "partial")
	}
	if m["text"] != "Enter password: " {
		t.Errorf("text = %q, want %q", m["text"], "Enter password: ")
	}
}

func TestAgentEmitter_Stats(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	st := &fes.Stats{
		Stamina: 10, MaxStamina: 20,
		Strength: 3, MaxStrength: 6,
		Dexterity: 4, MaxDexterity: 8,
		Magic: 5, MaxMagic: 10,
		Score: 12345,
		Rank:  "hero",
		Level: 7,
		Weather: 2,
		DreamWord: "apple",
		Blind: true, Deaf: false, Dumb: true, Crippled: false,
		ResetMinutes: 30,
	}
	a.EmitStats(st)

	m := decodeOne(t, &buf)
	if m["event"] != "stats" {
		t.Errorf("event = %q, want %q", m["event"], "stats")
	}
	checks := map[string]any{
		"stamina":       float64(10),
		"max_stamina":   float64(20),
		"strength":      float64(3),
		"max_strength":  float64(6),
		"dexterity":     float64(4),
		"max_dexterity": float64(8),
		"magic":         float64(5),
		"max_magic":     float64(10),
		"score":         float64(12345),
		"rank":          "hero",
		"level":         float64(7),
		"weather":       float64(2),
		"dream_word":    "apple",
		"blind":         true,
		"deaf":          false,
		"dumb":          true,
		"crippled":      false,
		"reset_minutes": float64(30),
	}
	for k, want := range checks {
		if got := m[k]; got != want {
			t.Errorf("stats[%q] = %v (%T), want %v (%T)", k, got, got, want, want)
		}
	}
}

func TestAgentEmitter_DreamWordSet(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.EmitDreamWord("banana")

	m := decodeOne(t, &buf)
	if m["event"] != "dreamword" {
		t.Errorf("event = %q, want %q", m["event"], "dreamword")
	}
	if m["word"] != "banana" {
		t.Errorf("word = %q, want %q", m["word"], "banana")
	}
}

func TestAgentEmitter_DreamWordCleared(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.EmitDreamWord("")

	m := decodeOne(t, &buf)
	if m["event"] != "dreamword" {
		t.Errorf("event = %q, want %q", m["event"], "dreamword")
	}
	if m["word"] != "" {
		t.Errorf("word = %q, want empty string for cleared", m["word"])
	}
}

func TestAgentEmitter_Sent(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.EmitSent("wave")

	m := decodeOne(t, &buf)
	if m["event"] != "sent" {
		t.Errorf("event = %q, want %q", m["event"], "sent")
	}
	if m["text"] != "wave" {
		t.Errorf("text = %q, want %q", m["text"], "wave")
	}
}

func TestAgentEmitter_Error(t *testing.T) {
	var buf bytes.Buffer
	a := NewAgentEmitter(&buf)
	a.EmitError("not connected: hello")

	m := decodeOne(t, &buf)
	if m["event"] != "error" {
		t.Errorf("event = %q, want %q", m["event"], "error")
	}
	if m["text"] != "not connected: hello" {
		t.Errorf("text = %q, want %q", m["text"], "not connected: hello")
	}
}

func TestAgentEmitter_GoroutineSafety(t *testing.T) {
	var buf safeBuffer
	a := NewAgentEmitter(&buf)

	const goroutines = 20
	const perGoroutine = 50
	var wg sync.WaitGroup
	wg.Add(goroutines)
	for i := 0; i < goroutines; i++ {
		go func() {
			defer wg.Done()
			for j := 0; j < perGoroutine; j++ {
				a.EmitSent("wave")
			}
		}()
	}
	wg.Wait()

	// Every line must be valid JSON with event:"sent".
	data := buf.Bytes()
	lines := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
	if len(lines) != goroutines*perGoroutine {
		t.Fatalf("expected %d lines, got %d", goroutines*perGoroutine, len(lines))
	}

	// Timestamps must be non-decreasing: locking before timestamping ensures
	// write order and timestamp order agree.
	var prevTime time.Time
	for i, line := range lines {
		var m map[string]any
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			t.Errorf("line %d is not valid JSON: %v\n%s", i, err, line)
			continue
		}
		ts, ok := m["time"].(string)
		if !ok {
			t.Errorf("line %d missing string time field", i)
			continue
		}
		parsed, err := time.Parse(time.RFC3339Nano, ts)
		if err != nil {
			t.Errorf("line %d has unparseable time %q: %v", i, ts, err)
			continue
		}
		if parsed.Before(prevTime) {
			t.Errorf("line %d timestamp %v is before previous %v (out of order)", i, parsed, prevTime)
		}
		prevTime = parsed
	}
}

// safeBuffer wraps bytes.Buffer with a mutex for use in concurrent tests.
type safeBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (s *safeBuffer) Write(p []byte) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.buf.Write(p)
}

func (s *safeBuffer) Bytes() []byte {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.buf.Bytes()
}
