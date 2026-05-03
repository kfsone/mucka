package commands

import (
	"fmt"
	"os"
	"sync/atomic"

	"gioui.org/app"
	"gioui.org/io/system"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/fes"
	"github.com/kfsone/mucka/internal/network"
	"github.com/kfsone/mucka/internal/ui"
	"github.com/kfsone/mucka/internal/version"
)

// fkeyEditorOpen is a singleton guard: only one F-key editor window may be open at a time.
var fkeyEditorOpen atomic.Bool

// dotQuitHandler returns a HandlerFunc that exits the application.
func dotQuitHandler() HandlerFunc {
	return func(args []string) {
		os.Exit(0)
	}
}

// dotHelpHandler returns a HandlerFunc that lists all registered dot-commands.
// If any $-commands are registered it appends a "see also: $help" note.
func dotHelpHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		d.u.TextPanel.AppendText("Available commands:")
		for _, e := range d.dotReg.Entries() {
			d.u.TextPanel.AppendText("  " + e.Name + "  \u2014 " + e.Desc)
		}
		if len(d.reg.Entries()) > 0 {
			d.u.TextPanel.AppendText("  (see also: $help)")
		}
	}
}

// dollarHelpHandler returns a HandlerFunc that lists all registered $-commands.
func dollarHelpHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		d.u.TextPanel.AppendText("Available $ commands:")
		for _, e := range d.reg.Entries() {
			d.u.TextPanel.AppendText("  " + e.Name + "  \u2014 " + e.Desc)
		}
	}
}

// connectToProfile is the single authoritative connect path. It looks up
// profileName in d.cfg, closes any existing connection, creates a new Conn
// wired with all callbacks (including SPM lifecycle callbacks), and starts
// the connect goroutine.
func connectToProfile(d *Dispatcher, profileName string) {
	if d.cfg == nil {
		d.u.TextPanel.AppendText("No configuration loaded.")
		return
	}
	profile, deprecated, ok := config.LookupProfile(d.cfg.Servers, profileName)
	if !ok {
		d.u.TextPanel.AppendText(fmt.Sprintf("Unknown server profile: %q", profileName))
		return
	}
	if deprecated {
		d.u.TextPanel.AppendText(fmt.Sprintf("Warning: profile name %q is deprecated; please use %q", profileName, config.ProfilePrefix+profileName))
	}
	if d.conn != nil {
		d.conn.Close()
	}
	invalidate := func() {}
	if d.w != nil {
		invalidate = d.w.Invalidate
	}
	conn := network.NewConn(d.u.TextPanel, invalidate)
	conn.StatsUpdated = func(s *fes.Stats) {
		d.u.SetStats(s)
		d.w.Invalidate()
	}
	conn.DreamWordUpdated = func(word string) {
		d.u.SetDreamWord(word)
		d.w.Invalidate()
	}
	conn.ConnFailed = func() {
		if d.spmProfile != "" {
			name := profileName
			d.pendingModal.Store(&name)
			if d.w != nil {
				d.w.Invalidate()
			}
		}
	}
	conn.ConnLost = func() {
		if d.spmProfile != "" {
			if d.w != nil {
				d.w.Perform(system.ActionClose)
			}
		}
	}
	d.cancelStreams()
	conn.Connect(profile)
	if d.w != nil {
		d.w.Option(app.Title(version.AppName + " " + version.String() + " — " + profileName))
	}
	d.conn = conn
}

// dotConnectHandler returns a HandlerFunc that connects to a named server profile.
// Usage: .connect <profile-name>
func dotConnectHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		if len(args) == 0 {
			d.u.TextPanel.AppendText("Usage: .connect <profile>")
			return
		}
		connectToProfile(d, args[0])
		d.spmProfile = args[0]
	}
}

// dotDisconnectHandler returns a HandlerFunc that closes the current connection
// and deactivates SPM.
func dotDisconnectHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		if d.conn != nil {
			d.conn.Close()
		}
		d.spmProfile = ""
		d.u.TextPanel.AppendText("Disconnected.")
	}
}

// dotFKeysHandler opens the F-key binding editor window (singleton).
func dotFKeysHandler(d *Dispatcher) {
	if !fkeyEditorOpen.CompareAndSwap(false, true) {
		return // already open
	}
	d.fkeysMu.RLock()
	currentFKeys := d.fkeys
	d.fkeysMu.RUnlock()

	ui.OpenFKeyEditor(
		d.fonts,
		currentFKeys,
		func(fk config.FKeyConfig) {
			d.SetFKeys(fk)
			d.w.Invalidate()
		},
		func(fk config.FKeyConfig) error {
			d.SetFKeys(fk)
			d.w.Invalidate()
			return config.SaveFKeys(config.Path(), fk)
		},
		func() {
			fkeyEditorOpen.Store(false)
		},
	)
}

