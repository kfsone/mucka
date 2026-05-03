// Package network provides TCP connectivity with telnet negotiation and
// MUD2 auto-login for mucka.
package network

import (
	"bufio"
	"fmt"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/core"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/mud2"
)
			
// Telnet command bytes.
const (
	telnetSE   = 240
	telnetSB   = 250
	telnetWILL = 251
	telnetWONT = 252
	telnetDO   = 253
	telnetDONT = 254
	telnetIAC  = 255

	// Option codes.
	optEcho         = 1
	optSGA          = 3
	optTermType     = 24
	optNAWS         = 31
)

// loginState tracks where we are in the MUD2 auto-login sequence.
type loginState int

const (
	stateWaitLogin   loginState = iota
	stateWaitAccount            // received "login: ", sent login
	stateWaitPassword           // received "Account ID: ", sent account
	stateDone                   // matched "assword:" substring (tolerates password:/Password: capitalisation), sent password
)

// Conn manages a single TCP connection to a MUD server.
type Conn struct {
	sink          core.TextSink
	invalidate    func()
	sendCh        chan string
	closeCh       chan struct{}
	closeOnce     sync.Once
	conn          net.Conn
	mu            sync.Mutex
	sgaDone       bool // WILL SGA has been responded to once
	connected     atomic.Bool
	connecting    atomic.Bool
	stats         fes.Stats
	profile       config.ServerProfile // active connection profile (Width/Height used for NAWS)
	// fesPending counts FES triggers that have been sent but whose FES packet
	// response has not yet been received. Incremented before each trigger is
	// queued; decremented when the matching packet arrives. While > 0, any
	// *-prefixed line that is not itself a valid FES packet is treated as a
	// text-format FES response line and suppressed from the display.
	fesPending atomic.Int32
	// ColorMap, when non-nil, receives the ANSI color→semantic-type mappings
	// parsed from /AL responses. It is populated at game entry (after /AL is
	// sent automatically) and updated in-place as /ASfbN lines arrive.
	ColorMap *mud2.ColorMap
	// StatsUpdated is called (from the reader goroutine) whenever stats change
	// due to a FES packet or a matched ScanLine. May be nil.
	StatsUpdated  func(*fes.Stats)
	// DreamWordUpdated is called when the dream word is set or cleared.
	// Empty string means cleared. May be nil. Called from reader goroutine.
	DreamWordUpdated func(string)
	// ConnFailed is called from the Connect goroutine when net.DialTimeout returns
	// an error (after the error message is written to sink). May be nil.
	ConnFailed func()
	// ConnLost is called from the reader goroutine when an established connection
	// drops due to a read error (after "Connection closed." is written). May be nil.
	ConnLost func()

	dreamWordMu  sync.RWMutex // protects dreamWordStr
	dreamWordStr string
}

// NewConn creates a Conn that posts output to sink and calls invalidate after
// each update. invalidate must be non-nil; use (&core.NopInvalidator{}).Invalidate
// in headless contexts.
func NewConn(sink core.TextSink, invalidate func()) *Conn {
	return &Conn{
		sink:       sink,
		invalidate: invalidate,
		sendCh:     make(chan string, 64),
		closeCh:    make(chan struct{}),
	}
}

// IsConnected reports whether the connection is currently active.
func (c *Conn) IsConnected() bool {
	return c.connected.Load()
}

// DreamWord returns the current dream word, or "" if none.
func (c *Conn) DreamWord() string {
	c.dreamWordMu.RLock()
	defer c.dreamWordMu.RUnlock()
	return c.dreamWordStr
}

func (c *Conn) updateDreamWord(word string) {
	c.dreamWordMu.Lock()
	c.dreamWordStr = word
	c.dreamWordMu.Unlock()
	if c.DreamWordUpdated != nil {
		c.DreamWordUpdated(word)
	}
}

// IsConnecting reports whether a dial is currently in progress.
func (c *Conn) IsConnecting() bool {
	return c.connecting.Load()
}

// Connect dials the server described by profile in a background goroutine,
// returning immediately. Status messages are delivered via the TextSink.
func (c *Conn) Connect(profile config.ServerProfile) {
	addr := net.JoinHostPort(profile.Host, fmt.Sprintf("%d", profile.Port))
	c.sink.AppendText(fmt.Sprintf("Connecting to %s...", addr))
	c.invalidate()
	c.connecting.Store(true)
	go func() {
		conn, err := net.DialTimeout("tcp", addr, 10*time.Second)
		if err != nil {
			c.connecting.Store(false)
			c.sink.AppendText("\x1b[31mConnection failed: " + err.Error() + "\x1b[0m")
			c.invalidate()
			if c.ConnFailed != nil {
				c.ConnFailed()
			}
			return
		}
		c.mu.Lock()
		c.conn = conn
		c.sgaDone = false
		c.mu.Unlock()
		c.connecting.Store(false)
		c.connected.Store(true)
		c.sink.AppendText("Connected.")
		c.invalidate()
		go c.reader(conn, profile)
		go c.writer(conn)
	}()
}

// Send queues a line for transmission. A trailing "\r\n" is appended automatically.
func (c *Conn) Send(line string) {
	if c.IsConnected() {
		c.sendCh <- line + "\r\n"
	}
}

// closeConn signals the writer goroutine and closes the underlying TCP
// connection. Safe to call from multiple goroutines; closeCh is closed at
// most once via closeOnce.
func (c *Conn) closeConn() {
	c.closeOnce.Do(func() { close(c.closeCh) })
	c.mu.Lock()
	if c.conn != nil {
		c.conn.Close()
	}
	c.mu.Unlock()
}

// Close terminates the connection.
func (c *Conn) Close() {
	if !c.connected.Swap(false) {
		return
	}
	c.closeConn()
}

// fesPollLoop sends a FES trigger every 10 seconds while connected and in game,
// mirroring Clio's behaviour. Exits when done or closeCh is closed.
func (c *Conn) fesPollLoop(done <-chan struct{}) {
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-c.closeCh:
			return
		case <-done:
			return
		case <-ticker.C:
			if c.IsConnected() {
				c.fesPending.Add(1)
				c.sendCh <- string(fes.TriggerBytes)
			}
		}
	}
}

// writer drains sendCh and writes to the TCP connection.
func (c *Conn) writer(conn net.Conn) {
	for {
		select {
		case <-c.closeCh:
			return
		case data := <-c.sendCh:
			if _, err := fmt.Fprint(conn, data); err != nil {
				return
			}
		}
	}
}

// reader reads from the TCP connection, strips telnet negotiation, buffers
// lines, and appends them to the panel.
func (c *Conn) reader(conn net.Conn, profile config.ServerProfile) {
	c.profile = profile
	br := bufio.NewReader(conn)
	var (
		lineBuf     []byte
		state       = stateWaitLogin
		ansiState   ansi.State
		gameEntered bool
		fesDone     chan struct{} // non-nil while FES polling is active
	)

	// exitGame stops FES polling and resets state so re-entry can restart it.
	exitGame := func() {
		if gameEntered {
			gameEntered = false
			close(fesDone)
			fesDone = nil
			c.fesPending.Store(0)
		}
	}

	defer func() {
		c.updateDreamWord("") // clear dream word on disconnect
		c.connected.Store(false)
		exitGame() // stop fesPollLoop if still running
		c.closeConn()
	}()

	for {
		b, err := br.ReadByte()
		if err != nil {
			if c.IsConnected() {
				c.sink.AppendText("\x1b[31mConnection closed.\x1b[0m")
				c.invalidate()
				if c.ConnLost != nil {
					c.ConnLost()
				}
			}
			return
		}

		if b == telnetIAC {
			// IAC IAC = escaped literal 0xFF byte in the data stream.
			if peeked, err := br.Peek(1); err == nil && len(peeked) == 1 && peeked[0] == telnetIAC {
				br.ReadByte() //nolint:errcheck // consume the second IAC
				lineBuf = append(lineBuf, telnetIAC)
			} else {
				resp := c.handleTelnet(br)
				if len(resp) > 0 {
					c.mu.Lock()
					conn.Write(resp) //nolint:errcheck
					c.mu.Unlock()
				}
			}
			continue
		}

		lineBuf = append(lineBuf, b)

		// Check for login automaton triggers only on space bytes: all
		// login prompts ("login: ", "Account ID: ", "assword: ") end
		// with a space, so this avoids an allocation on every byte.
		if state != stateDone && b == ' ' {
			line := latin1ToUTF8(lineBuf)
			state = c.runLoginAutomaton(state, line, profile)
		}

		if b == '\n' {
			// Strip dream-word protocol bytes before display.
			if processed, finalWord, changed := extractDreamWord(lineBuf); changed {
				lineBuf = processed
				c.updateDreamWord(finalWord)
			}
			text := strings.TrimRight(latin1ToUTF8(lineBuf), "\r\n")

			// Check for FES packet: strip ANSI codes, strip leading '*' chars (MUD prompt),
			// then attempt to parse as 15-field FES data. The server embeds FES fields on
			// the prompt line so the '*' prefix is expected.
			stateCopy := ansiState
			spans := ansi.ParseStateful(text, &stateCopy)
			plainText := spansToText(spans)
			body := strings.TrimLeft(plainText, "*")
			ansiState = stateCopy // advance ANSI state regardless of FES or not

			switch {
			case len(body) < len(plainText) && len(body) == 0:
				// Bare prompt terminator (*\r\n): server closes the current prompt line
				// before sending the FES response. Suppress from display, clear any
				// stale partial prompt character.
				c.sink.UpdatePartial(nil)
				c.invalidate()

			case len(body) < len(plainText) && fes.ParsePacket([]byte(body), &c.stats):
				// Valid FES packet (**stats or ANSI+*stats): suppress from display,
				// decrement the pending-trigger counter, and notify the callback.
				c.decrementFesPending()
				if c.StatsUpdated != nil {
					c.StatsUpdated(&c.stats)
				}
				c.sink.UpdatePartial(nil)
				c.invalidate()

			case len(body) < len(plainText) && c.fesPending.Load() > 0 && fes.ScanLine(plainText, &c.stats):
				// Text-format FES response line (e.g. "*Your stamina is 25.",
				// "*(Persona saved on …)"): positively identified by ScanLine.
				// Suppress from display; stats already updated by ScanLine above.
				if c.StatsUpdated != nil {
					c.StatsUpdated(&c.stats)
				}
				c.sink.UpdatePartial(nil)
				c.invalidate()

			default:
				// Normal text line: check for /AL color-map response first.
				// ParseALLine validates the full /ASfbN format (two color letters +
				// type number 0–60) before returning true, so false positives on
				// arbitrary game text are extremely unlikely.
				if c.ColorMap != nil && c.ColorMap.ParseALLine(plainText) {
					// /ASfbN color-map response: update the map and suppress from display.
					c.sink.UpdatePartial(nil)
					c.invalidate()
				} else {
					// Regular text: display and scan for embedded stats.
					c.sink.AppendSpans(spans)
					c.invalidate()
					if fes.ScanLine(plainText, &c.stats) && c.StatsUpdated != nil {
						c.StatsUpdated(&c.stats)
					}
					// MUD2 client-mode escape or "Option:" menu prompt: stop FES polling
					// so we don't spam the server while at the client menu.
					if gameEntered && mud2.IsClientModeSignal(lineBuf, plainText) {
						exitGame()
					}
					// Detect game entry via the opening room name and start FES polling.
					if !gameEntered && state == stateDone && mud2.IsGameEntry(plainText) {
						gameEntered = true
						fesDone = make(chan struct{})
						c.fesPending.Add(1)
						c.sendCh <- string(fes.TriggerBytes)
						if c.ColorMap != nil {
							// Request the full color→semantic-type map from the server.
							c.sendCh <- "/AL\r\n"
						}
						go c.fesPollLoop(fesDone)
					}
				}
			}
			lineBuf = lineBuf[:0]
		} else if br.Buffered() == 0 {
			// No more data available right now — flush as partial so prompts appear.
			// Strip dream-word protocol bytes before display.
			if processed, finalWord, changed := extractDreamWord(lineBuf); changed {
				lineBuf = processed
				c.updateDreamWord(finalWord)
			}
			text := strings.TrimRight(latin1ToUTF8(lineBuf), "\r")
			stateCopy := ansiState // snapshot: don't advance real state
			spans := ansi.ParseStateful(text, &stateCopy)
			plainText := spansToText(spans)
			c.sink.UpdatePartial(spans)
			c.invalidate()
			// Also catch client-mode signals that arrive as prompts (no trailing \n).
			if gameEntered && mud2.IsClientModeSignal(lineBuf, plainText) {
				exitGame()
			}
		}
	}
}

// spansToText concatenates the plain text from a slice of ANSI spans.
func spansToText(spans []ansi.Span) string {
	if len(spans) == 0 {
		return ""
	}
	var sb strings.Builder
	for _, sp := range spans {
		sb.WriteString(sp.Text)
	}
	return sb.String()
}

// decrementFesPending decrements fesPending by one, clamping the result at
// zero so that an unexpected extra FES packet never leaves the counter
// negative. A compare-and-swap loop ensures the decrement is fully atomic
// even when the poll goroutine is concurrently incrementing the counter.
func (c *Conn) decrementFesPending() {
	for {
		old := c.fesPending.Load()
		if old <= 0 {
			c.fesPending.Store(0)
			return
		}
		if c.fesPending.CompareAndSwap(old, old-1) {
			return
		}
	}
}

// runLoginAutomaton checks the accumulated line buffer for login prompts and
// sends credentials. Returns the updated state.
func (c *Conn) runLoginAutomaton(state loginState, line string, profile config.ServerProfile) loginState {
	switch state {
	case stateWaitLogin:
		if strings.Contains(line, "login: ") {
			c.sendCh <- profile.Login + "\r\n"
			return stateWaitAccount
		}
	case stateWaitAccount:
		if strings.Contains(line, "Account ID: ") {
			c.sendCh <- profile.Account + "\r\n"
			return stateWaitPassword
		}
	case stateWaitPassword:
		if strings.Contains(line, "assword:") {
			c.sendCh <- profile.Password + "\r\n"
			return stateDone
		}
	}
	return state
}

// handleTelnet reads the rest of a telnet command (IAC already consumed) and
// returns the bytes to send back, or nil.
func (c *Conn) handleTelnet(br *bufio.Reader) []byte {
	cmd, err := br.ReadByte()
	if err != nil {
		return nil
	}

	switch cmd {
	case telnetWILL:
		return c.handleWill(br)
	case telnetWONT:
		br.ReadByte() // consume option, ignore
		return nil
	case telnetDO:
		return c.handleDo(br)
	case telnetDONT:
		br.ReadByte() // consume option, ignore
		return nil
	case telnetSB:
		return c.handleSB(br)
	default:
		return nil
	}
}

// handleWill processes IAC WILL <opt>.
func (c *Conn) handleWill(br *bufio.Reader) []byte {
	opt, err := br.ReadByte()
	if err != nil {
		return nil
	}
	switch opt {
	case optSGA:
		// Only respond once to avoid loops.
		c.mu.Lock()
		done := c.sgaDone
		c.sgaDone = true
		c.mu.Unlock()
		if !done {
			return []byte{telnetIAC, telnetDO, opt}
		}
		return nil
	default:
		return []byte{telnetIAC, telnetDONT, opt}
	}
}

// handleDo processes IAC DO <opt>.
func (c *Conn) handleDo(br *bufio.Reader) []byte {
	opt, err := br.ReadByte()
	if err != nil {
		return nil
	}
	switch opt {
	case optEcho:
		return []byte{telnetIAC, telnetWONT, opt}
	case optTermType:
		return []byte{telnetIAC, telnetWILL, opt}
	case optNAWS:
		// Agree and send window size using the configured dimensions.
		w, h := uint16(c.profile.Width), uint16(c.profile.Height)
		return []byte{
			telnetIAC, telnetWILL, optNAWS,
			telnetIAC, telnetSB, optNAWS,
			byte(w >> 8), byte(w), byte(h >> 8), byte(h),
			telnetIAC, telnetSE,
		}
	default:  // esp 32, 33, 35, 36, 37, 39:
		return []byte{telnetIAC, telnetWONT, opt}
	}
}

// handleSB reads and processes an IAC SB ... IAC SE sub-negotiation.
func (c *Conn) handleSB(br *bufio.Reader) []byte {
	opt, err := br.ReadByte()
	if err != nil {
		return nil
	}

	// Read until IAC SE, collecting the sub-option bytes.
	var sub []byte
	for {
		b, err := br.ReadByte()
		if err != nil {
			return nil
		}
		if b == telnetIAC {
			end, err := br.ReadByte()
			if err != nil {
				return nil
			}
			if end == telnetSE {
				break
			}
			sub = append(sub, b, end)
		} else {
			sub = append(sub, b)
		}
	}

	// Handle TERMINAL-TYPE SEND (1).
	if opt == optTermType && len(sub) > 0 && sub[0] == 1 {
		termName := []byte("ansi")
		resp := []byte{telnetIAC, telnetSB, optTermType, 0}
		resp = append(resp, termName...)
		resp = append(resp, telnetIAC, telnetSE)
		return resp
	}

	return nil
}

