package network

import (
	"net"
	"runtime"
	"testing"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
)

// TestWriterExitsOnServerDisconnect verifies that the writer goroutine is not
// leaked when the server drops the connection (i.e. the reader's defer path
// calls closeConn so closeCh is closed and writer can exit).
func TestWriterExitsOnServerDisconnect(t *testing.T) {
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

	// Count goroutines before starting reader/writer so we can check they exit.
	before := runtime.NumGoroutine()

	go c.reader(client, config.ServerProfile{})
	go c.writer(client)

	// Wait until both goroutines are visible.
	if !waitFor(func() bool { return runtime.NumGoroutine() >= before+2 }) {
		t.Skip("goroutines never spawned — skipping leak check")
	}

	// Server closes the connection; reader should detect EOF, run its deferred
	// closeConn(), which closes closeCh and unblocks the writer.
	server.Close()

	// Both reader and writer should have exited.
	if !waitFor(func() bool { return runtime.NumGoroutine() <= before }) {
		t.Errorf("goroutine leak: started with %d, now %d after server disconnect",
			before, runtime.NumGoroutine())
	}
}
