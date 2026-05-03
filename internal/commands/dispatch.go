package commands

import (
	"context"
	"strings"
	"sync"
	"sync/atomic"

	"gioui.org/app"
	"gioui.org/font"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/network"
	"github.com/kfsone/mucka/internal/ui"
)

const (
	modeNormal = 0
	modeLess   = 1
	modeModal  = 2 // waiting for repeat/retry/cancel input after connection failure
)

// Dispatcher wires the command Registry to the UI.
type Dispatcher struct {
	w         *app.Window
	u         *ui.UI
	cfg       *config.Config
	conn      *network.Conn
	reg       *Registry // $-commands
	dotReg    *Registry // .-commands
	mode      int
	lessPages [][]string
	lessPage  int
	savedHint string
	fonts     []font.FontFace

	// SPM (Single Profile Mode) state
	spmProfile   string // non-empty = SPM active; holds current profile name
	modalProfile string // profile name shown in modal prompt
	pendingModal atomic.Pointer[string] // set from background goroutine; drained in Handle

	fkeysMu sync.RWMutex
	fkeys   config.FKeyConfig

	streamMu     sync.Mutex
	cancelStream context.CancelFunc

	logFileName string // non-empty = currently logging; holds the open file path
}

// NewDispatcher creates a Dispatcher, registers all commands, and sets up UI.OnSubmit.
// If initialProfile is non-empty, SPM is activated and a connection attempt is made immediately.
func NewDispatcher(w *app.Window, u *ui.UI, cfg *config.Config, fonts []font.FontFace, initialProfile string) *Dispatcher {
	d := &Dispatcher{
		w:      w,
		u:      u,
		cfg:    cfg,
		reg:    NewRegistry(),
		dotReg: NewRegistry(),
		fonts:  fonts,
		fkeys:  cfg.FKeys,
	}
	d.reg.Register("$stream", "stream a file to the text panel line by line", streamHandler(w, u.TextPanel, d))
	d.reg.Register("$source", "replay input tokens from a file", sourceHandler(w, u.TextPanel, u.InputLine, d))
	d.reg.Register("$less", "page through a file", lessHandler(d))
	d.reg.Register("$help", "list available $ commands", dollarHelpHandler(d))

	d.dotReg.Register(".help", "list available commands", dotHelpHandler(d))
	d.dotReg.Register(".quit", "exit the application", dotQuitHandler())
	d.dotReg.Register(".connect", "connect to a server profile", dotConnectHandler(d))
	d.dotReg.Register(".disconnect", "disconnect from server", dotDisconnectHandler(d))
	d.dotReg.Register(".fkeys", "open the F-key binding editor", func(args []string) { dotFKeysHandler(d) })
	d.dotReg.Register(".log", "start/stop logging to a file", dotLogHandler(d))

	u.OnSubmit = d.Handle
	u.ConnStatus = d.ConnStatus
	u.InputLine.DreamWordProvider = func() string {
		if d.conn == nil {
			return ""
		}
		return d.conn.DreamWord()
	}
	u.InputLine.FKeyProvider = func(mod, key string) string {
		return d.GetFKey(mod, key)
	}

	if initialProfile != "" {
		d.spmProfile = initialProfile
		connectToProfile(d, initialProfile)
	}

	return d
}

// GetFKey returns the binding for a modifier ("none"/"shift"/"ctrl") and key name ("F1"-"F12").
func (d *Dispatcher) GetFKey(mod, name string) string {
	d.fkeysMu.RLock()
	defer d.fkeysMu.RUnlock()
	return d.fkeys.GetCmd(mod, name)
}

// SetFKeys replaces the current fkey bindings.
func (d *Dispatcher) SetFKeys(fk config.FKeyConfig) {
	d.fkeysMu.Lock()
	d.fkeys = fk
	d.fkeysMu.Unlock()
}

// newStreamCtx cancels any in-flight $stream/$source goroutine and returns a
// fresh context for the new one.
func (d *Dispatcher) newStreamCtx() context.Context {
	d.streamMu.Lock()
	defer d.streamMu.Unlock()
	if d.cancelStream != nil {
		d.cancelStream()
	}
	ctx, cancel := context.WithCancel(context.Background())
	d.cancelStream = cancel
	return ctx
}

// cancelStreams cancels any in-flight $stream/$source goroutines without
// starting a new one.
func (d *Dispatcher) cancelStreams() {
	d.streamMu.Lock()
	defer d.streamMu.Unlock()
	if d.cancelStream != nil {
		d.cancelStream()
	}
}

// ConnStatus returns the current connection state for use by the status bar.
func (d *Dispatcher) ConnStatus() (connecting, connected bool) {
	if d.conn == nil {
		return false, false
	}
	return d.conn.IsConnecting(), d.conn.IsConnected()
}

// Handle dispatches a submitted input string.
func (d *Dispatcher) Handle(text string) {
	// Drain any pending modal request posted from a background goroutine.
	if ptr := d.pendingModal.Swap(nil); ptr != nil {
		d.enterModalMode(*ptr)
	}

	switch d.mode {
	case modeLess:
		switch strings.TrimSpace(text) {
		case "q", "Q":
			d.exitLessMode()
		default:
			if d.lessPage >= len(d.lessPages) {
				d.u.TextPanel.AppendText("-- END --")
				d.exitLessMode()
				return
			}
			page := d.lessPages[d.lessPage]
			d.lessPage++
			for _, line := range page {
				d.u.TextPanel.AppendText(line)
			}
			if d.lessPage >= len(d.lessPages) {
				d.u.TextPanel.AppendText("-- END --")
				d.exitLessMode()
			}
		}
		return

	case modeModal:
		t := strings.TrimSpace(text)
		switch {
		case t == "r" || strings.EqualFold(t, "repeat"):
			d.mode = modeNormal
			connectToProfile(d, d.modalProfile)
		case t == "R" || strings.EqualFold(t, "retry"):
			d.mode = modeNormal
			cfg, _ := config.Load()
			d.cfg = cfg
			connectToProfile(d, d.modalProfile)
		default: // c, cancel, or anything else
			d.mode = modeNormal
			d.spmProfile = ""
			d.modalProfile = ""
			d.u.TextPanel.AppendText("Connection cancelled.")
		}
		return
	}

	cmd := Tokenise(text)
	switch cmd.Type {
	case Plain:
		if cmd.Name == "" {
			return
		}
		// Forward to server if connected and login is complete; otherwise echo locally.
		if d.conn != nil && d.conn.IsConnected() {
			d.conn.Send(text)
		} else {
			d.u.TextPanel.AppendText(text)
		}
	case Dot:
		if !d.dotReg.Dispatch(cmd) {
			d.u.TextPanel.AppendText("unknown .command: " + cmd.Name[1:])
		}
	case Dollar:
		if !d.reg.Dispatch(cmd) {
			d.u.TextPanel.AppendText("$unknown: " + cmd.Name[1:])
		}
	}
}

func (d *Dispatcher) enterLessMode(pages [][]string) {
	d.savedHint = d.u.InputLine.Hint()
	d.u.InputLine.SetHint("--More-- (space/enter=next, q=quit)")
	d.mode = modeLess
	d.lessPages = pages
	d.lessPage = 0
}

func (d *Dispatcher) exitLessMode() {
	d.mode = modeNormal
	d.u.InputLine.SetHint(d.savedHint)
	d.lessPages = nil
}

func (d *Dispatcher) enterModalMode(profile string) {
	d.mode = modeModal
	d.modalProfile = profile
	d.u.TextPanel.AppendText("Connection failed. [r]epeat / [R]etry (reload config) / [c]ancel:")
}

