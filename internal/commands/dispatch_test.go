package commands

import (
	"bytes"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"unsafe"

	"github.com/kfsone/mucka/internal/ansi"
	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/ui"
)

// panelText reads both the committed 'lines' and the pending 'pendingLines'
// fields of a TextPanel via unsafe reflect, returning each line's concatenated
// span text. Used only in tests.
func panelText(p *ui.TextPanel) []string {
	rv := reflect.ValueOf(p).Elem()

	collect := func(fieldName string) []string {
		f := rv.FieldByName(fieldName)
		fp := reflect.NewAt(f.Type(), unsafe.Pointer(f.UnsafeAddr())).Elem()
		all := fp.Interface().([][]ansi.Span)
		var out []string
		for _, spans := range all {
			var sb strings.Builder
			for _, s := range spans {
				sb.WriteString(s.Text)
			}
			out = append(out, sb.String())
		}
		return out
	}

	result := collect("lines")
	result = append(result, collect("pendingLines")...)
	return result
}

// newTestDispatcher constructs a minimal Dispatcher without a real *app.Window.
// Only $less is registered; $stream/$source are not since they capture the window.
// No dot-commands are registered so .quit etc. remain "unknown" in these tests.
func newTestDispatcher() (*Dispatcher, *ui.UI) {
	u := ui.New()
	d := &Dispatcher{
		w:      nil,
		u:      u,
		cfg:    nil,
		reg:    NewRegistry(),
		dotReg: NewRegistry(),
	}
	d.reg.Register("$less", "page through file contents", lessHandler(d))
	return d, u
}

func TestDispatchPlainText(t *testing.T) {
	d, u := newTestDispatcher()
	d.Handle("hello world")
	lines := panelText(u.TextPanel)
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	if lines[0] != "hello world" {
		t.Errorf("expected %q, got %q", "hello world", lines[0])
	}
}

func TestDispatchEmptyInput(t *testing.T) {
	d, u := newTestDispatcher()
	d.Handle("")
	lines := panelText(u.TextPanel)
	if len(lines) != 0 {
		t.Errorf("expected no panel output for empty input, got %v", lines)
	}
}

func TestDispatchWhitespaceInput(t *testing.T) {
	d, u := newTestDispatcher()
	d.Handle("   ")
	lines := panelText(u.TextPanel)
	if len(lines) != 0 {
		t.Errorf("expected no panel output for whitespace-only input, got %v", lines)
	}
}

func TestDispatchUnknownDollarCommand(t *testing.T) {
	d, u := newTestDispatcher()
	d.Handle("$foobar")
	lines := panelText(u.TextPanel)
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	want := "$unknown: foobar"
	if lines[0] != want {
		t.Errorf("expected %q, got %q", want, lines[0])
	}
}

func TestDispatchDotCommand(t *testing.T) {
	d, u := newTestDispatcher()
	d.Handle(".quit")
	lines := panelText(u.TextPanel)
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	want := "unknown .command: quit"
	if lines[0] != want {
		t.Errorf("expected %q, got %q", want, lines[0])
	}
}

func TestDispatchLessModeQuit(t *testing.T) {
	d, _ := newTestDispatcher()
	d.enterLessMode([][]string{{"page2"}})
	if d.mode != modeLess {
		t.Fatal("expected less mode after enterLessMode")
	}
	d.Handle("q")
	if d.mode != modeNormal {
		t.Error("expected normal mode after q in less mode")
	}
}

func TestDispatchLessModeQuitUppercase(t *testing.T) {
	d, _ := newTestDispatcher()
	d.enterLessMode([][]string{{"page2"}})
	d.Handle("Q")
	if d.mode != modeNormal {
		t.Error("expected normal mode after Q in less mode")
	}
}

func TestDispatchLessModeAdvance(t *testing.T) {
	d, u := newTestDispatcher()
	d.enterLessMode([][]string{{"p2line"}, {"p3line"}})
	// Any non-q input advances to next page.
	d.Handle(" ")
	lines := panelText(u.TextPanel)
	if len(lines) == 0 {
		t.Fatal("expected page content to be appended on advance")
	}
	if lines[0] != "p2line" {
		t.Errorf("expected %q, got %q", "p2line", lines[0])
	}
}

func TestDispatchLessModeEnd(t *testing.T) {
	d, u := newTestDispatcher()
	// Single remaining page — advancing past it should print "-- END --" and exit.
	d.enterLessMode([][]string{{"last"}})
	d.Handle("") // advance past the last page
	lines := panelText(u.TextPanel)
	if len(lines) == 0 {
		t.Fatal("expected output after last page")
	}
	last := lines[len(lines)-1]
	if last != "-- END --" {
		t.Errorf("expected last line to be %q, got %q", "-- END --", last)
	}
	if d.mode != modeNormal {
		t.Error("expected normal mode after exhausting pages")
	}
}

// TestDotHelpIncludesFKeys verifies that .fkeys appears in the .help output
// when it is registered (as it is in NewDispatcher).
func TestDotHelpIncludesFKeys(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{w: nil, u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	d.dotReg.Register(".help", "list commands", dotHelpHandler(d))
	d.dotReg.Register(".fkeys", "open the F-key binding editor", func(args []string) {})

	d.Handle(".help")

	lines := panelText(u.TextPanel)
	found := false
	for _, l := range lines {
		if strings.Contains(l, ".fkeys") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("expected '.fkeys' in .help output; got: %v", lines)
	}
}

// TestDotHelpSeeAlsoPresentWhenDollarCmdsRegistered verifies that .help output
// includes "see also: $help" when dollar-commands are registered.
func TestDotHelpSeeAlsoPresentWhenDollarCmdsRegistered(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{w: nil, u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	d.reg.Register("$foo", "foo cmd", func(args []string) {})
	d.dotReg.Register(".help", "list commands", dotHelpHandler(d))

	d.Handle(".help")

	lines := panelText(u.TextPanel)
	found := false
	for _, l := range lines {
		if strings.Contains(l, "see also: $help") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("expected 'see also: $help' in output; got: %v", lines)
	}
}

// TestDotHelpSeeAlsoAbsentWhenNoDollarCmds verifies that .help omits "see also"
// when no dollar-commands are registered.
func TestDotHelpSeeAlsoAbsentWhenNoDollarCmds(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{w: nil, u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	d.dotReg.Register(".help", "list commands", dotHelpHandler(d))

	d.Handle(".help")

	lines := panelText(u.TextPanel)
	for _, l := range lines {
		if strings.Contains(l, "see also") {
			t.Errorf("unexpected 'see also' in output when no dollar-cmds; got line: %q", l)
		}
	}
}

// TestDollarHelpListsAllEntriesSorted verifies $help output contains every
// registered dollar-command name, in sorted order.
func TestDollarHelpListsAllEntriesSorted(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{w: nil, u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	d.reg.Register("$zzz", "z cmd", func(args []string) {})
	d.reg.Register("$aaa", "a cmd", func(args []string) {})
	d.reg.Register("$help", "list $ commands", dollarHelpHandler(d))

	d.Handle("$help")

	lines := panelText(u.TextPanel)
	var names []string
	for _, l := range lines {
		// Lines for entries are formatted as "  $name  — desc"
		trimmed := strings.TrimSpace(l)
		if strings.HasPrefix(trimmed, "$") {
			name := strings.Fields(trimmed)[0]
			names = append(names, name)
		}
	}
	want := []string{"$aaa", "$help", "$zzz"}
	if !reflect.DeepEqual(names, want) {
		t.Errorf("$help output names: got %v, want %v", names, want)
	}
}

// TestDispatchUnknownDotCommand verifies that an unknown dot-command (not in dotReg)
// produces the expected error message rather than panicking.
func TestDispatchUnknownDotCommandNotInDotReg(t *testing.T) {
	d, u := newTestDispatcher() // dotReg is empty in newTestDispatcher
	d.Handle(".frobnicate")
	lines := panelText(u.TextPanel)
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	want := "unknown .command: frobnicate"
	if lines[0] != want {
		t.Errorf("expected %q, got %q", want, lines[0])
	}
}

// ── .log command tests ────────────────────────────────────────────────────

// logTestDispatcher builds a Dispatcher with .log registered and no window.
func logTestDispatcher(cfg *config.Config) (*Dispatcher, *ui.UI) {
	u := ui.New()
	d := &Dispatcher{
		w:      nil,
		u:      u,
		cfg:    cfg,
		reg:    NewRegistry(),
		dotReg: NewRegistry(),
	}
	d.dotReg.Register(".log", "start/stop logging", dotLogHandler(d))
	return d, u
}

// nopWriteCloser wraps a *bytes.Buffer as an io.WriteCloser for testing.
type nopWriteCloser struct{ *bytes.Buffer }

func (nopWriteCloser) Close() error { return nil }

var _ io.WriteCloser = nopWriteCloser{}

func TestDotLogNoArgsNoTemplateShowsUsage(t *testing.T) {
	d, u := logTestDispatcher(nil)
	d.Handle(".log")
	lines := panelText(u.TextPanel)
	if len(lines) == 0 {
		t.Fatal("expected output from .log with no args and no config")
	}
	if !strings.Contains(lines[0], "Usage") {
		t.Errorf("expected usage message, got %q", lines[0])
	}
}

func TestDotLogOffWhenNotLogging(t *testing.T) {
	d, u := logTestDispatcher(nil)
	d.Handle(".log off")
	lines := panelText(u.TextPanel)
	if len(lines) == 0 {
		t.Fatal("expected output from .log off when not logging")
	}
	if !strings.Contains(lines[0], "Not currently logging") {
		t.Errorf("expected 'Not currently logging' message, got %q", lines[0])
	}
}

func TestDotLogOffIsCaseInsensitive(t *testing.T) {
	d, u := logTestDispatcher(nil)
	d.Handle(".log OFF")
	lines := panelText(u.TextPanel)
	if len(lines) == 0 || !strings.Contains(lines[0], "Not currently logging") {
		t.Errorf("expected 'Not currently logging', got %v", lines)
	}
}

func TestDotLogFilenameOpensFile(t *testing.T) {
	tmp := t.TempDir()
	logPath := filepath.Join(tmp, "test.log")

	d, u := logTestDispatcher(nil)
	d.Handle(".log " + logPath)

	lines := panelText(u.TextPanel)
	if len(lines) == 0 || !strings.Contains(lines[0], logPath) {
		t.Errorf("expected confirmation with path %q, got %v", logPath, lines)
	}
	if d.logFileName != logPath {
		t.Errorf("logFileName = %q, want %q", d.logFileName, logPath)
	}

	// Ensure the file was actually created.
	if _, err := os.Stat(logPath); err != nil {
		t.Errorf("log file not created: %v", err)
	}

	// Clean up.
	d.u.TextPanel.StopLog()
}

func TestDotLogWritesToFile(t *testing.T) {
	tmp := t.TempDir()
	logPath := filepath.Join(tmp, "out.log")

	d, _ := logTestDispatcher(nil)
	d.Handle(".log " + logPath)

	// Append text after logging started.
	d.u.TextPanel.AppendText("logged line")

	d.u.TextPanel.StopLog()
	d.logFileName = ""

	data, err := os.ReadFile(logPath)
	if err != nil {
		t.Fatalf("reading log file: %v", err)
	}
	// The "Logging to …" confirmation line and "logged line" should both appear.
	if !strings.Contains(string(data), "logged line") {
		t.Errorf("log file missing 'logged line'; content: %q", string(data))
	}
}

func TestDotLogOffStopsLogging(t *testing.T) {
	tmp := t.TempDir()
	logPath := filepath.Join(tmp, "stop.log")

	d, _ := logTestDispatcher(nil)
	d.Handle(".log " + logPath)
	d.Handle(".log off")

	if d.logFileName != "" {
		t.Errorf("logFileName should be empty after .log off, got %q", d.logFileName)
	}
}

func TestDotLogNoArgsShowsStatusWhenLogging(t *testing.T) {
	tmp := t.TempDir()
	logPath := filepath.Join(tmp, "status.log")

	d, u := logTestDispatcher(nil)
	d.Handle(".log " + logPath)

	// Clear panel lines seen so far via a fresh read.
	_ = panelText(u.TextPanel)

	// Calling .log with no args while logging should report the filename.
	d.Handle(".log")
	lines := panelText(u.TextPanel)
	// Find the status message (last appended).
	found := false
	for _, l := range lines {
		if strings.Contains(l, logPath) && strings.Contains(l, "Logging to") {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("expected status line with %q, got %v", logPath, lines)
	}

	d.u.TextPanel.StopLog()
}

func TestDotLogWithLogDir(t *testing.T) {
	tmp := t.TempDir()
	cfg := config.Default()
	cfg.General.LogDir = tmp

	d, u := logTestDispatcher(cfg)
	d.Handle(".log myfile.log")

	lines := panelText(u.TextPanel)
	wantPath := filepath.Join(tmp, "myfile.log")
	if d.logFileName != wantPath {
		t.Errorf("logFileName = %q, want %q", d.logFileName, wantPath)
	}
	found := false
	for _, l := range lines {
		if strings.Contains(l, wantPath) {
			found = true
			break
		}
	}
	if !found {
		t.Errorf("expected confirmation with %q, got %v", wantPath, lines)
	}

	d.u.TextPanel.StopLog()
}

func TestDotLogNoArgsUsesLogFileTemplate(t *testing.T) {
	tmp := t.TempDir()
	cfg := config.Default()
	cfg.General.LogDir = tmp
	// Use a fixed suffix so we know what the generated name looks like.
	cfg.General.LogFileT = "session-2006.log"

	d, u := logTestDispatcher(cfg)
	d.Handle(".log")

	lines := panelText(u.TextPanel)
	if len(lines) == 0 {
		t.Fatal("expected output after .log with log-file-t configured")
	}
	if strings.Contains(lines[0], "Usage") {
		t.Errorf("did not expect usage message when log-file-t is set; got %q", lines[0])
	}
	if d.logFileName == "" {
		t.Error("expected logFileName to be set after auto-start from template")
	}

	d.u.TextPanel.StopLog()
}
