package network

import (
	"net"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// TestConnectIsNonBlocking verifies that Connect() returns before the TCP dial
// completes. A listener is started on a random local port; it accepts the
// connection but never sends data, so the login automaton never fires. We only
// care that Connect() itself returned well under the 10 s DialTimeout.
func TestConnectIsNonBlocking(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("could not open listener: %v", err)
	}
	defer ln.Close()

	// Accept in the background and hold the connection open so the dial
	// succeeds but no data flows — the reader goroutine inside Connect() stays
	// alive for the test duration.
	go func() {
		conn, err := ln.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		buf := make([]byte, 1)
		conn.Read(buf) //nolint:errcheck
	}()

	addr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	sink := &core.BufferSink{}
	c := NewConn(sink, (&core.NopInvalidator{}).Invalidate)

	start := time.Now()
	c.Connect(profile) // must return immediately
	elapsed := time.Since(start)

	if elapsed > 500*time.Millisecond {
		t.Errorf("Connect() took %v; expected near-instant return (non-blocking)", elapsed)
	}

	// The "Connecting to …" message is enqueued synchronously before the goroutine starts.
	lines := sink.Snapshot()
	if len(lines) == 0 {
		t.Error("expected at least one status line in sink after Connect()")
	}
}

// TestConnectStatusMessageOnFailure verifies that a failed dial delivers an
// error message through the TextSink rather than panicking.
func TestConnectStatusMessageOnFailure(t *testing.T) {
	// Open and immediately close a listener so the port is not in use.
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("could not open ephemeral listener: %v", err)
	}
	addr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	ln.Close()

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	sink := &core.BufferSink{}
	c := NewConn(sink, (&core.NopInvalidator{}).Invalidate)
	c.Connect(profile)

	// Wait for the refused connection to propagate (fast on loopback, 2 s cap for CI).
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		time.Sleep(20 * time.Millisecond)
		if len(sink.Snapshot()) >= 2 {
			break
		}
	}

	lines := sink.Snapshot()
	// Expect: line 0 = "Connecting to …", line 1 = "Connection failed: …"
	if len(lines) < 2 {
		t.Fatalf("want ≥2 lines, got %d: %v", len(lines), lines)
	}
	if len(lines[1]) == 0 {
		t.Error("expected non-empty failure message in second sink line")
	}
}
