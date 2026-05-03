package commands

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"time"

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
		d.w.Option(app.Title(version.AppName + " v" + version.Version + " — " + profileName))
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

// resolveLogPath joins filename with cfg.General.LogDir when LogDir is
// configured and filename is not already an absolute path.
func resolveLogPath(cfg *config.Config, filename string) string {
	if cfg != nil && cfg.General.LogDir != "" && !filepath.IsAbs(filename) {
		return filepath.Join(cfg.General.LogDir, filename)
	}
	return filename
}

// startLog opens filename for append-writing and activates logging on
// d.u.TextPanel. It reports success or failure via the text panel.
func startLog(d *Dispatcher, filename string) {
	f, err := os.OpenFile(filename, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		d.u.TextPanel.AppendText(fmt.Sprintf("Cannot open log file %q: %v", filename, err))
		return
	}
	var linePrefix func() string
	if d.cfg != nil && d.cfg.General.LogFmt != "" {
		logFmt := d.cfg.General.LogFmt
		linePrefix = func() string { return time.Now().Format(logFmt) + " " }
	}
	d.u.TextPanel.SetLogWriter(f, linePrefix)
	d.logFileName = filename
	d.u.TextPanel.AppendText(fmt.Sprintf("Logging to %q.", filename))
}

// dotLogHandler returns a HandlerFunc for the .log command.
//
// Usage:
//
//	.log <filename>  — start logging to filename
//	.log off         — stop logging
//	.log             — if log-file-t is set in config, start logging using the
//	                   generated filename; if already logging, report the current
//	                   file; otherwise print usage.
func dotLogHandler(d *Dispatcher) HandlerFunc {
	return func(args []string) {
		if len(args) > 0 && strings.EqualFold(args[0], "off") {
			if d.u.TextPanel.StopLog() {
				d.logFileName = ""
				d.u.TextPanel.AppendText("Logging stopped.")
			} else {
				d.u.TextPanel.AppendText("Not currently logging.")
			}
			return
		}

		if len(args) > 0 {
			// Explicit filename provided.
			filename := resolveLogPath(d.cfg, args[0])
			startLog(d, filename)
			return
		}

		// No arguments: report current status or auto-start from template.
		if d.logFileName != "" {
			d.u.TextPanel.AppendText(fmt.Sprintf("Logging to %q.", d.logFileName))
			return
		}
		if d.cfg != nil && d.cfg.General.LogFileT != "" {
			filename := resolveLogPath(d.cfg, time.Now().Format(d.cfg.General.LogFileT))
			startLog(d, filename)
			return
		}
		d.u.TextPanel.AppendText("Usage: .log <filename> | .log off")
	}
}

