package network

import (
	"net"
	"strings"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/fes"
)

// TestIACIAC_InjectsByte verifies that a 0xFF 0xFF pair in the raw TCP stream
// is collapsed to a single literal 0xFF byte in lineBuf (and rendered as ÿ,
// the Latin-1/U+00FF character) rather than being treated as a telnet command.
func TestIACIAC_InjectsByte(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Raw stream: 'A' + IAC IAC (escaped literal 0xFF) + 'Z' + newline.
	// After IAC IAC processing lineBuf = ['A', 0xFF, 'Z', '\n'].
	// latin1ToUTF8(0xFF) = U+00FF = "ÿ", so the line should read "AÿZ".
	server.Write([]byte{'A', telnetIAC, telnetIAC, 'Z', '\n'})
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	lines := sink.Snapshot()
	if len(lines) == 0 {
		t.Fatal("no output lines received")
	}
	found := false
	for _, line := range lines {
		if strings.Contains(line, "AÿZ") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("no line containing AÿZ in sink output: %v", lines)
	}
}

// TestFESPacket_CallsCallback verifies the end-to-end FES packet path:
// A "**"-prefixed line with valid fields triggers StatsUpdated and is NOT
// forwarded to the text sink.
func TestFESPacket_CallsCallback(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := make(chan *fes.Stats, 1)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Real mudii.co.uk format: "**" prefix then space-separated fields, newline-terminated.
	server.Write([]byte("**25 30 10 15 8 12 3 30 12345 N N N N 15 F\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	select {
	case s := <-called:
		if s.Stamina != 25 {
			t.Errorf("Stamina = %d, want 25", s.Stamina)
		}
		if s.MaxStamina != 30 {
			t.Errorf("MaxStamina = %d, want 30", s.MaxStamina)
		}
		if s.Score != 12345 {
			t.Errorf("Score = %d, want 12345", s.Score)
		}
	default:
		t.Error("StatsUpdated was not called for valid FES packet")
	}

	// The FES packet must not appear as a text line in the sink.
	for _, line := range sink.Snapshot() {
		if strings.HasPrefix(line, "**") {
			t.Errorf("FES packet prefix leaked into text sink: %q", line)
		}
	}
}

// TestFESPacket_ANSIPrefixedRealFormat verifies detection of the actual mudii.co.uk
// wire format: three ANSI escape sequences before the '*' prompt, then FES fields.
func TestFESPacket_ANSIPrefixedRealFormat(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := make(chan *fes.Stats, 1)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Real format from mudii.co.uk:
	// \x1b[1;37;40m\x1b[0;34;40m\x1b[1;34;40m*100 100 100 100 100 100 0 100 7652 N N N N 100 F\r\n
	line := "\x1b[1;37;40m\x1b[0;34;40m\x1b[1;34;40m*100 100 100 100 100 100 0 100 7652 N N N N 100 F\r\n"
	server.Write([]byte(line))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	select {
	case s := <-called:
		if s.Stamina != 100 || s.MaxStamina != 100 {
			t.Errorf("Stamina = %d/%d, want 100/100", s.Stamina, s.MaxStamina)
		}
		if s.Score != 7652 {
			t.Errorf("Score = %d, want 7652", s.Score)
		}
	default:
		t.Error("StatsUpdated was not called for ANSI-prefixed FES line")
	}

	// The FES line must not appear in the text sink.
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "7652") {
			t.Errorf("FES data leaked into text sink: %q", line)
		}
	}
}

// TestFESBarePrompt verifies that a bare "*\n" line (which the MUD2 server
// emits to terminate the prompt line before sending the FES response) is
// silently suppressed and never shown in the text panel.
func TestFESBarePrompt(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := make(chan *fes.Stats, 1)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Server terminates the prompt line then sends actual FES data — the "*\n"
	// must be suppressed and the FES data must be parsed normally.
	server.Write([]byte("*\n"))
	server.Write([]byte("**25 30 100 100 95 95 0 0 5000 N N N N 44 F\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// The bare "*" line must not appear in the sink.
	for _, line := range sink.Snapshot() {
		if line == "*" {
			t.Errorf("bare prompt line leaked into text sink")
		}
	}

	// Stats from the FES response must have been parsed.
	select {
	case s := <-called:
		if s.Stamina != 25 {
			t.Errorf("Stamina = %d, want 25", s.Stamina)
		}
	default:
		t.Error("StatsUpdated was not called after bare prompt + FES line")
	}
}

func TestFESPacket_MalformedBody(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := false
	c.StatsUpdated = func(s *fes.Stats) { called = true }

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// "**" prefix with too few fields.
	server.Write([]byte("**BAD FIELDS\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	if called {
		t.Error("StatsUpdated should not be called for malformed FES body")
	}
}
