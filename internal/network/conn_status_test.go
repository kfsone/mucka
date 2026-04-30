package network

import (
	"net"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// holdListener opens a TCP listener, accepts one connection, and holds it open
// until the returned close function is called or the listener closes.
// The returned addr is the listener's local address.
func holdListener(t *testing.T) (addr *net.TCPAddr, closeFn func()) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("holdListener: %v", err)
	}
	go func() {
		conn, err := ln.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		buf := make([]byte, 1)
		conn.Read(buf) //nolint:errcheck
	}()
	tcpAddr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	return tcpAddr, func() { ln.Close() }
}

// waitFor polls cond until it returns true or a 2-second deadline passes.
// Returns false if the deadline is reached.
func waitFor(cond func() bool) bool {
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return true
		}
		time.Sleep(10 * time.Millisecond)
	}
	return false
}

// TestIsConnecting_FalseBeforeConnect verifies the zero-value state.
func TestIsConnecting_FalseBeforeConnect(t *testing.T) {
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	if c.IsConnecting() {
		t.Error("IsConnecting() should be false before any Connect() call")
	}
}

// TestIsConnected_FalseBeforeConnect verifies the zero-value state.
func TestIsConnected_FalseBeforeConnect(t *testing.T) {
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	if c.IsConnected() {
		t.Error("IsConnected() should be false before any Connect() call")
	}
}

// TestIsConnecting_TrueImmediatelyAfterConnect verifies that connecting is set
// to true before the background goroutine is launched, so it is observable
// synchronously after Connect() returns.
func TestIsConnecting_TrueImmediatelyAfterConnect(t *testing.T) {
	addr, closeFn := holdListener(t)
	defer closeFn()

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)

	c.Connect(profile)

	if !c.IsConnecting() {
		t.Error("IsConnecting() should be true immediately after Connect() returns")
	}
}

// TestIsConnecting_FalseAfterDialFail verifies that the failure path in the
// goroutine resets connecting to false.
func TestIsConnecting_FalseAfterDialFail(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	ln.Close() // close immediately — next dial is refused

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	c.Connect(profile)

	if !waitFor(func() bool { return !c.IsConnecting() }) {
		t.Error("IsConnecting() should be false after dial failure")
	}
	if c.IsConnected() {
		t.Error("IsConnected() should be false after dial failure")
	}
}

// TestConnStateTransitions verifies the full state machine across a successful
// connect lifecycle:
//
//	initial:    connecting=false, connected=false
//	after Connect(): connecting=true,  connected=false
//	after dial ok:   connecting=false, connected=true
func TestConnStateTransitions(t *testing.T) {
	addr, closeFn := holdListener(t)
	defer closeFn()

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)

	// Initial state.
	if c.IsConnecting() || c.IsConnected() {
		t.Fatal("precondition failed: both flags should be false before Connect()")
	}

	c.Connect(profile)

	// Synchronously observable: connecting flipped before goroutine launch.
	if !c.IsConnecting() {
		t.Error("IsConnecting() should be true immediately after Connect()")
	}
	if c.IsConnected() {
		t.Error("IsConnected() should be false while still connecting")
	}

	// Wait for dial to succeed and goroutine to update atomics.
	if !waitFor(func() bool { return c.IsConnected() }) {
		t.Fatal("IsConnected() never became true after successful dial")
	}
	if c.IsConnecting() {
		t.Error("IsConnecting() should be false after successful dial completes")
	}
}

// TestConnStateTransitions_FailPath verifies that after a refused connection
// neither flag is left set true.
func TestConnStateTransitions_FailPath(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	ln.Close()

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}
	c := NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	c.Connect(profile)

	// connecting starts true
	if !c.IsConnecting() {
		t.Error("IsConnecting() should be true immediately after Connect()")
	}

	// After failure both should settle to false.
	if !waitFor(func() bool { return !c.IsConnecting() && !c.IsConnected() }) {
		t.Errorf("after fail: connecting=%v connected=%v (want both false)",
			c.IsConnecting(), c.IsConnected())
	}
}
