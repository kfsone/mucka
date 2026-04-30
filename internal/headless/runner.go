// Package headless implements a stdin/stdout MUD client loop with no GUI dependency.
package headless

import (
	"bufio"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/network"
)

// opKind describes what action a parsed script line should trigger.
type opKind int

const (
	opSkip       opKind = iota // blank line or comment
	opSleep                    // sleep <duration>
	opSend                     // send text to MUD
	opDisconnect               // .disconnect
	opQuit                     // .quit
)

// op is a parsed script line.
type op struct {
	kind     opKind
	text     string        // for opSend
	duration time.Duration // for opSleep
}

// ParseLine parses a single script line into an op.
func ParseLine(line string) op {
	line = strings.TrimSpace(line)
	if line == "" || strings.HasPrefix(line, "#") {
		return op{kind: opSkip}
	}
	if line == ".quit" {
		return op{kind: opQuit}
	}
	if line == ".disconnect" {
		return op{kind: opDisconnect}
	}
	if strings.HasPrefix(line, "sleep ") {
		raw := strings.TrimPrefix(line, "sleep ")
		d, err := time.ParseDuration(strings.TrimSpace(raw))
		if err == nil {
			return op{kind: opSleep, duration: d}
		}
	}
	return op{kind: opSend, text: line}
}

// connector is the subset of network.Conn used by the headless runner,
// extracted as an interface to allow testing.
type connector interface {
	IsConnected() bool
	Send(line string)
	Close()
}

// Run executes the headless client loop. If scriptFile != "", commands are
// read from that file; otherwise from os.Stdin. profileName, if non-empty,
// triggers an automatic .connect before the script/stdin loop begins.
func Run(cfg *config.Config, profileName, scriptFile string) error {
	sink := &core.StdioSink{}
	nop := &core.NopInvalidator{}
	conn := network.NewConn(sink, nop.Invalidate)
	conn.StatsUpdated = func(st *fes.Stats) {
		fmt.Printf("[STATS] sta=%d/%d str=%d/%d dex=%d/%d mag=%d/%d score=%d rank=%s\n",
			st.Stamina, st.MaxStamina,
			st.Strength, st.MaxStrength,
			st.Dexterity, st.MaxDexterity,
			st.Magic, st.MaxMagic,
			st.Score, st.Rank)
	}

	if profileName != "" {
		profile, ok := cfg.Servers[profileName]
		if !ok {
			return fmt.Errorf("profile %q not found in config", profileName)
		}
		conn.Connect(profile)
		// Poll until connected or 15s timeout.
		deadline := time.Now().Add(15 * time.Second)
		for conn.IsConnecting() {
			if time.Now().After(deadline) {
				return fmt.Errorf("timed out waiting for connection to %q", profileName)
			}
			time.Sleep(100 * time.Millisecond)
		}
	}

	var input *os.File
	if scriptFile != "" {
		f, err := os.Open(scriptFile)
		if err != nil {
			return fmt.Errorf("open script: %w", err)
		}
		defer f.Close()
		input = f
	} else {
		input = os.Stdin
	}

	return runScript(conn, bufio.NewScanner(input))
}

// runScript drives the main read-and-execute loop over scanner lines.
func runScript(conn connector, scanner *bufio.Scanner) error {
	for scanner.Scan() {
		o := ParseLine(scanner.Text())
		switch o.kind {
		case opSkip:
			// nothing
		case opSleep:
			time.Sleep(o.duration)
		case opQuit:
			conn.Close()
			return nil
		case opDisconnect:
			conn.Close()
		case opSend:
			if conn.IsConnected() {
				conn.Send(o.text)
				fmt.Printf(">>> %s\n", o.text)
			} else {
				fmt.Fprintf(os.Stderr, "not connected: %s\n", o.text)
			}
		}
	}
	conn.Close()
	return nil
}
