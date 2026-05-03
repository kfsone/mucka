package network

import (
	"net"
	"strings"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/mud2"
)

// TestColorMapLine_SuppressedAndParsed verifies that /ASfbN color-map response
// lines are parsed into the ColorMap and suppressed from the text sink.
func TestColorMapLine_SuppressedAndParsed(t *testing.T) {
	sink := &core.BufferSink{}
	cm := mud2.NewColorMap()
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
		ColorMap:   cm,
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	// Send a /AL color-map response: type 3 (ROOM-NAME) → green fg on default bg.
	// Also send a regular text line to ensure normal display still works.
	server.Write([]byte("/ASGn3\nhello world\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	// The /ASfbN line must not appear in the text sink.
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "/AS") {
			t.Errorf("/ASfbN color-map line leaked into text sink: %q", line)
		}
	}

	// Regular text must still appear.
	found := false
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "hello world") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("regular text line missing from sink; got: %v", sink.Snapshot())
	}

	// Color map must have been updated: green FG + default BG → type 3.
	greenFG := ansi.StandardColor(2)
	if got := cm.Lookup(greenFG, ansi.DefaultBG); got != 3 {
		t.Errorf("ColorMap.Lookup(green, defaultBG) = %d, want 3", got)
	}
}

// TestColorMapLine_NotSuppressedWithoutColorMap verifies that /ASfbN lines are
// NOT suppressed when no ColorMap is attached (they pass through as regular text).
func TestColorMapLine_NotSuppressedWithoutColorMap(t *testing.T) {
	sink := &core.BufferSink{}
	c := &Conn{
		sink:       sink,
		invalidate: (&core.NopInvalidator{}).Invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
		// ColorMap intentionally nil
	}
	c.connected.Store(true)

	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()

	go c.reader(client, config.ServerProfile{})

	server.Write([]byte("/ASGn3\n"))
	time.Sleep(80 * time.Millisecond)
	server.Close()
	time.Sleep(20 * time.Millisecond)

	found := false
	for _, line := range sink.Snapshot() {
		if strings.Contains(line, "/ASGn3") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("/ASfbN line should pass through to sink when no ColorMap is attached")
	}
}

