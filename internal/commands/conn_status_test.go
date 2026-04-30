package commands

import (
	"net"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/network"
)

// TestConnStatusNilConn verifies that ConnStatus() returns (false, false) when
// d.conn has never been set (nil guard in dispatch.go).
func TestConnStatusNilConn(t *testing.T) {
	d, _ := newTestDispatcher()
	// d.conn is nil by default in newTestDispatcher.
	connecting, connected := d.ConnStatus()
	if connecting {
		t.Error("ConnStatus() connecting should be false when d.conn is nil")
	}
	if connected {
		t.Error("ConnStatus() connected should be false when d.conn is nil")
	}
}

// TestConnStatusDelegates_WhileConnecting verifies that ConnStatus() mirrors
// conn.IsConnecting()==true immediately after a Connect() call on a live listener.
func TestConnStatusDelegates_WhileConnecting(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	defer ln.Close()
	// Accept and hold so the dial succeeds but no data flows.
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

	d, _ := newTestDispatcher()
	c := network.NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	d.conn = c

	c.Connect(profile)

	// Immediately after Connect() the connecting flag is true.
	connecting, connected := d.ConnStatus()
	if !connecting {
		t.Error("ConnStatus() connecting should be true while dial is in progress")
	}
	if connected {
		t.Error("ConnStatus() connected should be false while dial is in progress")
	}
}

// TestConnStatusDelegates_AfterConnected verifies that ConnStatus() reflects
// connected==true once the dial has succeeded.
func TestConnStatusDelegates_AfterConnected(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	defer ln.Close()
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

	d, _ := newTestDispatcher()
	c := network.NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	d.conn = c

	c.Connect(profile)

	// Poll until the dial completes and connected is set.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if c.IsConnected() {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	_, connected := d.ConnStatus()
	if !connected {
		t.Error("ConnStatus() connected should be true after successful dial")
	}
	connecting, _ := d.ConnStatus()
	if connecting {
		t.Error("ConnStatus() connecting should be false after successful dial")
	}
}

// TestConnStatusDelegates_AfterDialFail verifies ConnStatus() returns (false,false)
// once a refused connection resolves.
func TestConnStatusDelegates_AfterDialFail(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr, _ := net.ResolveTCPAddr("tcp", ln.Addr().String())
	ln.Close() // immediate close → refused

	profile := config.ServerProfile{Host: addr.IP.String(), Port: addr.Port}

	d, _ := newTestDispatcher()
	c := network.NewConn(&core.BufferSink{}, (&core.NopInvalidator{}).Invalidate)
	d.conn = c

	c.Connect(profile)

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if !c.IsConnecting() {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	connecting, connected := d.ConnStatus()
	if connecting {
		t.Error("ConnStatus() connecting should be false after dial failure")
	}
	if connected {
		t.Error("ConnStatus() connected should be false after dial failure")
	}
}
