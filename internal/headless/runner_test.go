package headless

import (
	"bufio"
	"strings"
	"testing"
	"time"
)

func TestParseScriptLine_Sleep(t *testing.T) {
	o := ParseLine("sleep 5s")
	if o.kind != opSleep {
		t.Fatalf("want opSleep, got %v", o.kind)
	}
	if o.duration != 5*time.Second {
		t.Fatalf("want 5s, got %v", o.duration)
	}
}

func TestParseScriptLine_SleepMs(t *testing.T) {
	o := ParseLine("sleep 500ms")
	if o.kind != opSleep {
		t.Fatalf("want opSleep, got %v", o.kind)
	}
	if o.duration != 500*time.Millisecond {
		t.Fatalf("want 500ms, got %v", o.duration)
	}
}

func TestParseScriptLine_Comment(t *testing.T) {
	o := ParseLine("# comment")
	if o.kind != opSkip {
		t.Fatalf("want opSkip, got %v", o.kind)
	}
}

func TestParseScriptLine_BlankLine(t *testing.T) {
	o := ParseLine("")
	if o.kind != opSkip {
		t.Fatalf("want opSkip, got %v", o.kind)
	}
}

func TestParseScriptLine_Quit(t *testing.T) {
	o := ParseLine(".quit")
	if o.kind != opQuit {
		t.Fatalf("want opQuit, got %v", o.kind)
	}
}

func TestParseScriptLine_Disconnect(t *testing.T) {
	o := ParseLine(".disconnect")
	if o.kind != opDisconnect {
		t.Fatalf("want opDisconnect, got %v", o.kind)
	}
}

func TestParseScriptLine_Send(t *testing.T) {
	o := ParseLine("wave")
	if o.kind != opSend {
		t.Fatalf("want opSend, got %v", o.kind)
	}
	if o.text != "wave" {
		t.Fatalf("want 'wave', got %q", o.text)
	}
}

func TestParseScriptLine_SendWithComma(t *testing.T) {
	o := ParseLine("act, testily")
	if o.kind != opSend {
		t.Fatalf("want opSend, got %v", o.kind)
	}
	if o.text != "act, testily" {
		t.Fatalf("want 'act, testily', got %q", o.text)
	}
}

func TestParseScriptLine_WhitespaceOnly(t *testing.T) {
	o := ParseLine("   \t  ")
	if o.kind != opSkip {
		t.Fatalf("want opSkip for whitespace-only line, got %v", o.kind)
	}
}

func TestParseScriptLine_InvalidSleepFallsToSend(t *testing.T) {
	// An unrecognised duration should NOT be sent to the MUD as text;
	// treat it as opSend so the caller sees exactly what will be transmitted.
	o := ParseLine("sleep notaduration")
	if o.kind != opSend {
		t.Fatalf("want opSend for bad sleep arg, got %v", o.kind)
	}
	if o.text != "sleep notaduration" {
		t.Fatalf("want text preserved, got %q", o.text)
	}
}

func TestParseScriptLine_LeadingWhitespaceTrimmed(t *testing.T) {
	o := ParseLine("  .quit  ")
	if o.kind != opQuit {
		t.Fatalf("want opQuit after trimming whitespace, got %v", o.kind)
	}
}

// mockConn records calls for TestRunScript_Sequence.
type mockConn struct {
	connected bool
	sent      []string
	closeCount int
}

func (m *mockConn) IsConnected() bool { return m.connected }
func (m *mockConn) Send(line string)  { m.sent = append(m.sent, line) }
func (m *mockConn) Close()            { m.closeCount++; m.connected = false }

func TestRunScript_Sequence(t *testing.T) {
	script := strings.Join([]string{
		"# header comment",
		"wave",
		"sleep 0s",
		"act, testily",
		".quit",
		"should not execute",
	}, "\n")

	conn := &mockConn{connected: true}
	scanner := bufio.NewScanner(strings.NewReader(script))
	if err := runScript(conn, scanner); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	want := []string{"wave", "act, testily"}
	if len(conn.sent) != len(want) {
		t.Fatalf("sent %v, want %v", conn.sent, want)
	}
	for i, w := range want {
		if conn.sent[i] != w {
			t.Errorf("sent[%d] = %q, want %q", i, conn.sent[i], w)
		}
	}
	if conn.closeCount == 0 {
		t.Error("expected conn to be closed after .quit")
	}
}

func TestRunScript_DisconnectContinues(t *testing.T) {
	// .disconnect closes the conn but the script continues running.
	script := strings.Join([]string{
		".disconnect",
		"should not be sent", // conn is now disconnected
		".quit",
	}, "\n")

	conn := &mockConn{connected: true}
	scanner := bufio.NewScanner(strings.NewReader(script))
	if err := runScript(conn, scanner); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(conn.sent) != 0 {
		t.Errorf("expected no sends after disconnect, got %v", conn.sent)
	}
	if conn.closeCount < 1 {
		t.Error("expected at least one Close call")
	}
}

func TestRunScript_SendWhenDisconnected(t *testing.T) {
	// Lines that arrive when not connected are dropped (not sent to MUD).
	script := "hello world\n"

	conn := &mockConn{connected: false}
	scanner := bufio.NewScanner(strings.NewReader(script))
	if err := runScript(conn, scanner); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(conn.sent) != 0 {
		t.Errorf("expected no sends when disconnected, got %v", conn.sent)
	}
}
