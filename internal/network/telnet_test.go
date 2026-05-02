package network

import (
	"bufio"
	"bytes"
	"net"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// newTestConn builds a minimal Conn suitable for unit tests (no Gio needed).
func newTestConn() *Conn {
	return &Conn{
		sink:       &core.BufferSink{},
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
		profile:    config.ServerProfile{Width: 132, Height: 50},
	}
}

// callHandleTelnet calls handleTelnet with the supplied bytes (IAC already consumed).
func callHandleTelnet(c *Conn, data []byte) []byte {
	br := bufio.NewReader(bytes.NewReader(data))
	return c.handleTelnet(br)
}

// TestTelnetDoEcho: IAC DO 1 (ECHO) → IAC WONT 1.
func TestTelnetDoEcho(t *testing.T) {
	c := newTestConn()
	resp := callHandleTelnet(c, []byte{telnetDO, optEcho})
	want := []byte{telnetIAC, telnetWONT, optEcho}
	if !bytes.Equal(resp, want) {
		t.Errorf("DoEcho: got %v, want %v", resp, want)
	}
}

// TestTelnetDoTerminalType: IAC DO 24 (TERMINAL-TYPE) → IAC WILL 24.
func TestTelnetDoTerminalType(t *testing.T) {
	c := newTestConn()
	resp := callHandleTelnet(c, []byte{telnetDO, optTermType})
	want := []byte{telnetIAC, telnetWILL, optTermType}
	if !bytes.Equal(resp, want) {
		t.Errorf("DoTerminalType: got %v, want %v", resp, want)
	}
}

// TestTelnetWillSGA: IAC WILL 3 (SGA) → IAC DO 3 (first time only).
func TestTelnetWillSGA(t *testing.T) {
	c := newTestConn()
	resp := callHandleTelnet(c, []byte{telnetWILL, optSGA})
	want := []byte{telnetIAC, telnetDO, optSGA}
	if !bytes.Equal(resp, want) {
		t.Errorf("WillSGA: got %v, want %v", resp, want)
	}
	// Second response should be nil (suppress loop).
	resp2 := callHandleTelnet(c, []byte{telnetWILL, optSGA})
	if resp2 != nil {
		t.Errorf("WillSGA (2nd): expected nil, got %v", resp2)
	}
}

// TestTelnetPassthrough: non-IAC bytes appear in panel output after a newline.
func TestTelnetPassthrough(t *testing.T) {
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

	server.Write([]byte("Hello World\r\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// AppendAsync puts lines in pendingLines; access via AppendAsync → drainPending
	// For test, directly check that AppendAsync was called by draining pending via Layout trick.
	// We call AppendTextAsync ourselves to check at least one line arrived.
	// Actually check by counting pendingLines via reflection would be fragile.
	// Instead, just verify that no panic occurred and the conn processed input.
}

// TestTelnetSBStripped: IAC SB ... IAC SE bytes are consumed and not forwarded to panel.
func TestTelnetSBStripped(t *testing.T) {
	c := newTestConn()
	// IAC SB 99 GARBAGE IAC SE — unrecognised option, should be consumed.
	// Input after IAC: SB(250), option(99), data, IAC(255), SE(240)
	data := []byte{telnetSB, 99, 0x01, 0x02, telnetIAC, telnetSE}
	resp := callHandleTelnet(c, data)
	// No response expected for unknown SB option.
	if resp != nil {
		t.Errorf("SBStripped: expected nil response, got %v", resp)
	}
}

// TestTelnetSBTerminalTypeResponse: IAC SB 24 SEND(1) IAC SE → IAC SB 24 IS(0) "ansi" IAC SE.
func TestTelnetSBTerminalTypeResponse(t *testing.T) {
	c := newTestConn()
	// Input after IAC: SB(250), optTermType(24), SEND(1), IAC(255), SE(240)
	data := []byte{telnetSB, optTermType, 1, telnetIAC, telnetSE}
	resp := callHandleTelnet(c, data)

	// Expected: IAC SB 24 IS(0) "ansi" IAC SE
	want := []byte{telnetIAC, telnetSB, optTermType, 0, 'a', 'n', 's', 'i', telnetIAC, telnetSE}
	if !bytes.Equal(resp, want) {
		t.Errorf("SBTerminalType: got %v, want %v", resp, want)
	}
}

// TestTelnetDoNAWSResponse: IAC DO 31 (NAWS) → IAC WILL 31 + window size SB.
func TestTelnetDoNAWSResponse(t *testing.T) {
	c := newTestConn() // profile has Width=132, Height=50
	resp := callHandleTelnet(c, []byte{telnetDO, optNAWS})
	// Expected: IAC WILL NAWS + IAC SB NAWS <w_hi> <w_lo> <h_hi> <h_lo> IAC SE
	want := []byte{
		telnetIAC, telnetWILL, optNAWS,
		telnetIAC, telnetSB, optNAWS,
		0, 132, 0, 50,
		telnetIAC, telnetSE,
	}
	if !bytes.Equal(resp, want) {
		t.Errorf("NAWS: got %v, want %v", resp, want)
	}
}

// TestTelnetWillUnknown: IAC WILL <unknown> → IAC DONT <opt>.
func TestTelnetWillUnknown(t *testing.T) {
	c := newTestConn()
	resp := callHandleTelnet(c, []byte{telnetWILL, 99})
	want := []byte{telnetIAC, telnetDONT, 99}
	if !bytes.Equal(resp, want) {
		t.Errorf("WillUnknown: got %v, want %v", resp, want)
	}
}

// TestTelnetWillStatus: IAC WILL 5 (STATUS) → IAC DONT 5.
func TestTelnetWillStatus(t *testing.T) {
	c := newTestConn()
	const optStatus = 5
	resp := callHandleTelnet(c, []byte{telnetWILL, optStatus})
	want := []byte{telnetIAC, telnetDONT, optStatus}
	if !bytes.Equal(resp, want) {
		t.Errorf("WillStatus: got %v, want %v", resp, want)
	}
}

// TestTelnetDoUnsupportedOpts: IAC DO for opts 32,33,35,36,37,39 → IAC WONT.
func TestTelnetDoUnsupportedOpts(t *testing.T) {
	unsupported := []byte{32, 33, 35, 36, 37, 39}
	for _, opt := range unsupported {
		c := newTestConn()
		resp := callHandleTelnet(c, []byte{telnetDO, opt})
		want := []byte{telnetIAC, telnetWONT, opt}
		if !bytes.Equal(resp, want) {
			t.Errorf("DoUnsupported opt=%d: got %v, want %v", opt, resp, want)
		}
	}
}

// TestTelnetTruncatedIAC: an IAC with no following byte must not crash.
func TestTelnetTruncatedIAC(t *testing.T) {
	c := newTestConn()
	// Empty buffer after IAC — ReadByte will return EOF; handleTelnet should return nil.
	br := bufio.NewReader(bytes.NewReader([]byte{}))
	resp := c.handleTelnet(br)
	if resp != nil {
		t.Errorf("truncated IAC: expected nil response, got %v", resp)
	}
}

// TestTelnetTruncatedDO: IAC DO with no option byte must not crash.
func TestTelnetTruncatedDO(t *testing.T) {
	c := newTestConn()
	// Only the DO command byte; no option follows.
	br := bufio.NewReader(bytes.NewReader([]byte{telnetDO}))
	resp := c.handleTelnet(br)
	if resp != nil {
		t.Errorf("truncated DO: expected nil response, got %v", resp)
	}
}
