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

// TestFESBarePromptClearsPartial verifies that when the server sends a bare
// "*\n" prompt terminator (FES bare prompt), the stale partial prompt
// character is cleared from the sink rather than left visible.
func TestFESBarePromptClearsPartial(t *testing.T) {
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

	// Phase 1: server sends a bare prompt — reader flushes it as partial.
	server.Write([]byte("*"))
	if !waitFor(func() bool { return sink.SnapshotPartial() == "*" }) {
		t.Fatalf("phase 1: partial not set to '*': got %q", sink.SnapshotPartial())
	}

	// Phase 2: server sends bare FES prompt terminator "*\n".
	// The "*\n" line is a bare prompt — it must be suppressed AND the stale
	// partial must be cleared.
	server.Write([]byte("\n"))
	if !waitFor(func() bool { return sink.SnapshotPartial() == "" }) {
		t.Errorf("phase 2: partial not cleared after bare FES prompt: got %q", sink.SnapshotPartial())
	}

	// No complete lines should have been added for the bare prompt.
	for _, line := range sink.Snapshot() {
		if line == "*" {
			t.Errorf("bare FES prompt leaked into complete lines: %q", line)
		}
	}
}

// TestFESPacketClearsPartial verifies that when a valid FES packet is received
// (**stats\n), the stale partial prompt character is cleared from the sink.
func TestFESPacketClearsPartial(t *testing.T) {
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

	// Phase 1: server sends a bare prompt — reader flushes it as partial.
	server.Write([]byte("*"))
	if !waitFor(func() bool { return sink.SnapshotPartial() == "*" }) {
		t.Fatalf("phase 1: partial not set to '*': got %q", sink.SnapshotPartial())
	}

	// Phase 2: FES packet arrives — partial must be cleared, stats callback fired.
	server.Write([]byte("**25 30 100 100 95 95 0 0 5000 N N N N 44 F\n"))
	if !waitFor(func() bool { return sink.SnapshotPartial() == "" }) {
		t.Errorf("phase 2: partial not cleared after FES packet: got %q", sink.SnapshotPartial())
	}
	select {
	case s := <-called:
		if s.Stamina != 25 {
			t.Errorf("Stamina = %d, want 25", s.Stamina)
		}
	default:
		t.Error("StatsUpdated was not called after FES packet")
	}
	// The FES packet must not appear as a text line.
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "5000") {
			t.Errorf("FES data leaked into complete lines: %q", line)
		}
	}
}

// TestFESTextFormatSuppressedWhenPending verifies that text-format FES response
// lines (e.g. "*Your stamina is 25.") are suppressed from the display panel
// while a FES trigger is in flight (fesPending > 0), but stats are still
// extracted via ScanLine so the status bar remains accurate.
func TestFESTextFormatSuppressedWhenPending(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	// Simulate an in-flight FES trigger.
	c.fesPending.Store(1)

	called := make(chan *fes.Stats, 4)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Server sends text-format FES response lines followed by the FES packet.
	server.Write([]byte("*Your stamina is 25.\n"))
	server.Write([]byte("*(Persona saved on 01/01 with score 5000)\n"))
	server.Write([]byte("**25 30 100 100 95 95 0 0 5000 N N N N 44 F\n"))
	time.Sleep(100 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// None of the FES response lines (text-format or packet) must appear in
	// the text panel.
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "stamina") || strings.Contains(line, "Persona") || strings.Contains(line, "5000") {
			t.Errorf("FES response line leaked into text panel: %q", line)
		}
	}

	// Stats must have been extracted (Stamina updated by ScanLine or packet).
	if c.stats.Stamina != 25 {
		t.Errorf("Stamina = %d, want 25 (not updated from suppressed FES response)", c.stats.Stamina)
	}
	// StatsUpdated callback should have been called at least once.
	if len(called) == 0 {
		t.Error("StatsUpdated was never called for any suppressed FES response line")
	}
}

// TestFESTextFormatShownWhenNotPending verifies that text-format stat lines
// starting with '*' are displayed normally when no FES trigger is in flight
// (e.g. the user manually typed "sc"). Stats must also be extracted.
func TestFESTextFormatShownWhenNotPending(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)
	// fesPending is 0 (no in-flight trigger).

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	server.Write([]byte("*Your stamina is 42.\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// The line must appear in the text panel.
	found := false
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "stamina") {
			found = true
			break
		}
	}
	if !found {
		t.Error("text-format stat line not shown when no FES trigger is in flight")
	}

	// Stats must also be updated.
	if c.stats.Stamina != 42 {
		t.Errorf("Stamina = %d, want 42", c.stats.Stamina)
	}
}

// TestFESUnknownStarredLineShownWhenPending verifies that a '*'-prefixed line
// that is NOT a recognised FES text-format line is displayed normally even
// while a FES trigger is in flight. Previously the over-broad suppression
// would silently discard such lines.
func TestFESUnknownStarredLineShownWhenPending(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)
	c.fesPending.Store(1) // Simulate an in-flight trigger.

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Send a '*'-prefixed line that ScanLine will NOT recognise as a FES response.
	server.Write([]byte("*Unknown game message.\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// The unrecognised line must still reach the text panel.
	found := false
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "Unknown game message") {
			found = true
			break
		}
	}
	if !found {
		t.Error("unrecognised '*'-prefixed line was incorrectly suppressed while fesPending > 0")
	}
}

// TestCtrlDWeather_UpdatesStatsAndStripsFromDisplay verifies that a ctrl-d
// (0x04) weather sequence embedded in a server line is stripped from displayed
// text and used to update c.stats.Weather, then StatsUpdated is fired.
func TestCtrlDWeather_UpdatesStatsAndStripsFromDisplay(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := make(chan *fes.Stats, 4)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Server sends a normal line that contains a ctrl-d weather marker.
	// The 0x04 + 'F' pair must be stripped from display and Weather set to 'F'.
	server.Write(append([]byte("The sky is clear."), 0x04, 'F', '\n'))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// The ctrl-d bytes must not appear in the text panel.
	for _, line := range sink.Snapshot() {
		if strings.ContainsRune(line, 0x04) {
			t.Errorf("ctrl-d byte leaked into text sink: %q", line)
		}
	}

	// The line must reach the text panel (without the ctrl-d bytes).
	found := false
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "The sky is clear.") {
			found = true
			break
		}
	}
	if !found {
		t.Error("normal text line was suppressed (expected in sink)")
	}

	// StatsUpdated must have been called with Weather='F'.
	select {
	case s := <-called:
		if s.Weather != 'F' {
			t.Errorf("Weather = %d (%c), want %d ('F')", s.Weather, s.Weather, byte('F'))
		}
	default:
		t.Error("StatsUpdated was not called after ctrl-d weather sequence")
	}

	// c.stats.Weather must also be 'F' directly.
	if c.stats.Weather != 'F' {
		t.Errorf("c.stats.Weather = %d, want %d ('F')", c.stats.Weather, byte('F'))
	}
}

// TestCtrlDWeather_FESPacketPreservesCtrlDWeather verifies that when a FES
// packet's weather field is integer 0, the ctrl-d-sourced Weather value is
// preserved rather than overwritten.
func TestCtrlDWeather_FESPacketPreservesCtrlDWeather(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
	c.connected.Store(true)

	called := make(chan *fes.Stats, 4)
	c.StatsUpdated = func(s *fes.Stats) {
		cp := *s
		called <- &cp
	}

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Phase 1: ctrl-d sets weather to 'R'.
	server.Write(append([]byte("It starts to rain."), 0x04, 'R', '\n'))
	time.Sleep(60 * time.Millisecond)

	// Phase 2: FES packet arrives with weather field = 0 (should NOT overwrite 'R').
	server.Write([]byte("**25 30 100 100 95 95 0 0 5000 N N N N 44 0\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// Drain the callback channel; find the last stats update.
	var lastStats *fes.Stats
	for {
		select {
		case s := <-called:
			lastStats = s
			continue
		default:
		}
		break
	}

	if lastStats == nil {
		t.Fatal("StatsUpdated was never called")
	}
	if lastStats.Weather != 'R' {
		t.Errorf("Weather = %d (%c), want %d ('R') — FES packet with weather=0 should not overwrite ctrl-d value",
			lastStats.Weather, lastStats.Weather, byte('R'))
	}
}
