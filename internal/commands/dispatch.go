package commands

import (
	"strings"
	"sync"

	"gioui.org/app"
	"gioui.org/font"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/network"
	"github.com/kfsone/mucka/internal/ui"
)

const (
	modeNormal = 0
	modeLess   = 1
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

	fkeysMu sync.RWMutex
	fkeys   config.FKeyConfig
}

// NewDispatcher creates a Dispatcher, registers all commands, and sets up UI.OnSubmit.
func NewDispatcher(w *app.Window, u *ui.UI, cfg *config.Config, fonts []font.FontFace) *Dispatcher {
	d := &Dispatcher{
		w:      w,
		u:      u,
		cfg:    cfg,
		reg:    NewRegistry(),
		dotReg: NewRegistry(),
		fonts:  fonts,
		fkeys:  cfg.FKeys,
	}
	d.reg.Register("$stream", "stream a file to the text panel line by line", streamHandler(w, u.TextPanel))
	d.reg.Register("$source", "replay input tokens from a file", sourceHandler(w, u.TextPanel, u.InputLine))
	d.reg.Register("$less", "page through a file", lessHandler(d))
	d.reg.Register("$help", "list available $ commands", dollarHelpHandler(d))

	d.dotReg.Register(".help", "list available commands", dotHelpHandler(d))
	d.dotReg.Register(".quit", "exit the application", dotQuitHandler())
	d.dotReg.Register(".connect", "connect to a server profile", dotConnectHandler(d))
	d.dotReg.Register(".fkeys", "open the F-key binding editor", func(args []string) { dotFKeysHandler(d) })

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

// ConnStatus returns the current connection state for use by the status bar.
func (d *Dispatcher) ConnStatus() (connecting, connected bool) {
	if d.conn == nil {
		return false, false
	}
	return d.conn.IsConnecting(), d.conn.IsConnected()
}

// Handle dispatches a submitted input string.
func (d *Dispatcher) Handle(text string) {
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

