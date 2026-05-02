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

// Run executes the headless client loop using NDJSON (AgentEmitter) output.
// If scriptFile != "", commands are read from that file; otherwise from os.Stdin.
// profileName, if non-empty, triggers an automatic .connect before the loop.
func Run(cfg *config.Config, profileName, scriptFile string) error {
	return runWithEmitter(cfg, profileName, scriptFile, NewAgentEmitter(os.Stdout))
}

// RunStdio executes the headless client loop using plain-text (PlainEmitter) output,
// suitable for LLM harness use.
func RunStdio(cfg *config.Config, profileName, scriptFile string) error {
	return runWithEmitter(cfg, profileName, scriptFile, NewPlainEmitter(os.Stdout))
}

// runWithEmitter is the shared implementation used by Run and RunStdio.
func runWithEmitter(cfg *config.Config, profileName, scriptFile string, e emitter) error {
	nop := &core.NopInvalidator{}
	conn := network.NewConn(e, nop.Invalidate)
	conn.StatsUpdated = e.EmitStats
	conn.DreamWordUpdated = e.EmitDreamWord

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

	return runScript(conn, e, bufio.NewScanner(input))
}

// runScript drives the main read-and-execute loop over scanner lines.
// e may be nil, in which case sent/error events are silently suppressed.
func runScript(conn connector, e emitter, scanner *bufio.Scanner) error {
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
				if e != nil {
					e.EmitSent(o.text)
				}
			} else {
				if e != nil {
					e.EmitError("not connected: " + o.text)
				} else {
					fmt.Fprintf(os.Stderr, "not connected: %s\n", o.text)
				}
			}
		}
	}
	conn.Close()
	return nil
}
