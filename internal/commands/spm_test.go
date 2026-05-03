package commands

import (
	"net"
	"strings"
	"testing"
	"time"

	"github.com/kfsone/mucka/internal/config"
	"github.com/kfsone/mucka/internal/ui"
)

// newSPMDispatcher creates a minimal Dispatcher in modal mode for SPM tests.
// spmProfile is pre-set and the dispatcher is placed in modeModal with the
// given modalProfile.
func newSPMDispatcher(spmProfile, modalProfile string) (*Dispatcher, *ui.UI) {
	u := ui.New()
	d := &Dispatcher{
		w:            nil,
		u:            u,
		cfg:          nil, // no config — connectToProfile will fail early
		reg:          NewRegistry(),
		dotReg:       NewRegistry(),
		mode:         modeModal,
		spmProfile:   spmProfile,
		modalProfile: modalProfile,
	}
	d.dotReg.Register(".disconnect", "disconnect from server", dotDisconnectHandler(d))
	return d, u
}

// containsLine reports whether any panel line contains the given substring.
func containsLine(lines []string, substr string) bool {
	for _, l := range lines {
		if strings.Contains(l, substr) {
			return true
		}
	}
	return false
}

// --- modeModal transition tests ---

func TestModalModeCancelLower(t *testing.T) {
	d, u := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("c")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
	if d.spmProfile != "" {
		t.Errorf("spmProfile: got %q, want empty", d.spmProfile)
	}
	if d.modalProfile != "" {
		t.Errorf("modalProfile: got %q, want empty", d.modalProfile)
	}
	if !containsLine(panelText(u.TextPanel), "Connection cancelled.") {
		t.Error("expected 'Connection cancelled.' in panel output")
	}
}

func TestModalModeCancelWord(t *testing.T) {
	d, u := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("cancel")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
	if d.spmProfile != "" {
		t.Errorf("spmProfile: got %q, want empty", d.spmProfile)
	}
	if !containsLine(panelText(u.TextPanel), "Connection cancelled.") {
		t.Error("expected 'Connection cancelled.' in panel output")
	}
}

func TestModalModeCancelDefault(t *testing.T) {
	// Any unrecognised input should cancel.
	d, u := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("whatever")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
	if !containsLine(panelText(u.TextPanel), "Connection cancelled.") {
		t.Error("expected 'Connection cancelled.' in panel output")
	}
}

func TestModalModeRepeatLower(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("r")
	// With nil cfg, connectToProfile prints "No configuration loaded." and returns.
	// Mode must be reset to modeNormal.
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

func TestModalModeRepeatWord(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("repeat")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

func TestModalModeRepeatWordCaseInsensitive(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("REPEAT")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

func TestModalModeRetryUpper(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("R")
	// With nil cfg returned from config.Load(), connectToProfile prints error and returns.
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

func TestModalModeRetryWord(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("retry")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

func TestModalModeRetryWordCaseInsensitive(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("RETRY")
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal", d.mode)
	}
}

// TestModalRepeatVsRetryDistinction verifies that 'r' (lower) triggers repeat
// and 'R' (upper) triggers retry (config reload path). Both result in modeNormal,
// but they take different branches — ensure no cross-contamination.
func TestModalRepeatVsRetryDistinction(t *testing.T) {
	for _, tc := range []struct {
		input string
	}{
		{"r"},
		{"R"},
	} {
		d, _ := newSPMDispatcher("prof", "prof")
		d.Handle(tc.input)
		if d.mode != modeNormal {
			t.Errorf("input %q: mode = %d, want modeNormal", tc.input, d.mode)
		}
	}
}

// TestModalRepeatConnectsToModalProfile verifies that 'r' calls connectToProfile
// with d.modalProfile (we can verify the panel message from the failed connect).
func TestModalRepeatConnectsToModalProfile(t *testing.T) {
	d, u := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("r")
	// With nil cfg, connectToProfile emits "No configuration loaded."
	if !containsLine(panelText(u.TextPanel), "No configuration loaded") {
		t.Error("expected connectToProfile to be called (panel should show cfg error)")
	}
}

func TestModalRetryConnectsToModalProfile(t *testing.T) {
	d, u := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("R")
	// config.Load() may return nil cfg; connectToProfile then prints "No configuration loaded."
	// If a real config is found but profile is unknown, it prints an unknown profile error.
	lines := panelText(u.TextPanel)
	connected := containsLine(lines, "No configuration loaded") || containsLine(lines, "Unknown server profile")
	if !connected {
		t.Errorf("expected connectToProfile to be called; panel: %v", lines)
	}
}

// --- .disconnect tests ---

func TestDisconnectClearsSPMProfile(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{
		w:          nil,
		u:          u,
		cfg:        nil,
		reg:        NewRegistry(),
		dotReg:     NewRegistry(),
		spmProfile: "myprofile",
	}
	d.dotReg.Register(".disconnect", "disconnect from server", dotDisconnectHandler(d))

	d.Handle(".disconnect")

	if d.spmProfile != "" {
		t.Errorf("spmProfile: got %q, want empty after .disconnect", d.spmProfile)
	}
	if !containsLine(panelText(u.TextPanel), "Disconnected.") {
		t.Error("expected 'Disconnected.' in panel output")
	}
}

func TestDisconnectWithNilConnDoesNotPanic(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{
		w:      nil,
		u:      u,
		reg:    NewRegistry(),
		dotReg: NewRegistry(),
	}
	d.dotReg.Register(".disconnect", "disconnect from server", dotDisconnectHandler(d))
	// Should not panic when d.conn is nil.
	d.Handle(".disconnect")
}

// TestEnterModalModeSetsModeAndProfile verifies enterModalMode directly.
func TestEnterModalModeSetsModeAndProfile(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	d.enterModalMode("testprofile")
	if d.mode != modeModal {
		t.Errorf("mode: got %d, want modeModal", d.mode)
	}
	if d.modalProfile != "testprofile" {
		t.Errorf("modalProfile: got %q, want %q", d.modalProfile, "testprofile")
	}
	if !containsLine(panelText(u.TextPanel), "Connection failed.") {
		t.Error("expected modal prompt in panel output")
	}
}

// TestPendingModalDrainedInHandle verifies that a pending modal stored via
// pendingModal is drained and applied at the next Handle() call.
func TestPendingModalDrainedInHandle(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{u: u, reg: NewRegistry(), dotReg: NewRegistry()}
	prof := "draintest"
	d.pendingModal.Store(&prof)
	// Any Handle call should drain pendingModal.
	// We immediately cancel so Handle doesn't try to dispatch the modal text.
	d.Handle("c")
	// After draining, enterModalMode was called, then the 'c' input cancelled.
	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal after cancel", d.mode)
	}
	if d.spmProfile != "" {
		t.Errorf("spmProfile should be cleared after cancel")
	}
}

// TestModalModeRepeatPreservesSPMProfile verifies that 'r' leaves spmProfile
// set — SPM must remain active so a subsequent ConnFailed can still post a modal.
func TestModalModeRepeatPreservesSPMProfile(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("r")
	if d.spmProfile != "myprofile" {
		t.Errorf("spmProfile: got %q, want %q — SPM must stay active after repeat", d.spmProfile, "myprofile")
	}
}

// TestModalModeRetryPreservesSPMProfile verifies that 'R' leaves spmProfile
// set — SPM must remain active so a subsequent ConnFailed can still post a modal.
func TestModalModeRetryPreservesSPMProfile(t *testing.T) {
	d, _ := newSPMDispatcher("myprofile", "myprofile")
	d.Handle("R")
	if d.spmProfile != "myprofile" {
		t.Errorf("spmProfile: got %q, want %q — SPM must stay active after retry", d.spmProfile, "myprofile")
	}
}

// TestPendingModalOverwriteWhileAlreadyModal verifies that a second ConnFailed
// firing while already in modeModal (pendingModal overwrite) is handled: the
// next Handle drains the new profile and re-presents the modal, then the text
// passed to Handle is processed as the modal response.
func TestPendingModalOverwriteWhileAlreadyModal(t *testing.T) {
	u := ui.New()
	d := &Dispatcher{u: u, reg: NewRegistry(), dotReg: NewRegistry(), spmProfile: "profile1"}
	d.enterModalMode("profile1")

	// Simulate a second ConnFailed before the user has responded.
	prof2 := "profile2"
	d.pendingModal.Store(&prof2)

	// Handle("c") drains pendingModal (re-enters modal for profile2), then
	// processes "c" as the modal response → cancel.
	d.Handle("c")

	if d.mode != modeNormal {
		t.Errorf("mode: got %d, want modeNormal after cancel", d.mode)
	}
	if d.spmProfile != "" {
		t.Errorf("spmProfile should be cleared after cancel, got %q", d.spmProfile)
	}
}

// newRefusedProfile creates a TCP listener, closes it immediately so that a
// dial to that address will be refused, and returns a minimal Config and
// profile name targeting that address.
func newRefusedProfile(t *testing.T) (*config.Config, string) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	addr := ln.Addr().(*net.TCPAddr)
	ln.Close()
	cfg := &config.Config{
		Servers: map[string]config.ServerProfile{
			"profile.testprof": {Host: addr.IP.String(), Port: addr.Port},
		},
	}
	return cfg, "testprof"
}

// TestConnFailedSetsModalWhenSPMActive verifies the end-to-end path: when a
// dial fails and spmProfile is set, ConnFailed posts to pendingModal.
func TestConnFailedSetsModalWhenSPMActive(t *testing.T) {
	cfg, profName := newRefusedProfile(t)
	u := ui.New()
	d := &Dispatcher{u: u, cfg: cfg, reg: NewRegistry(), dotReg: NewRegistry()}
	// SPM active before connecting (mirrors the NewDispatcher initialProfile path).
	d.spmProfile = profName
	connectToProfile(d, profName)

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if d.pendingModal.Load() != nil {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}

	if d.pendingModal.Load() == nil {
		t.Error("expected pendingModal to be set after ConnFailed with SPM active")
	}
	if ptr := d.pendingModal.Load(); ptr != nil && *ptr != profName {
		t.Errorf("pendingModal profile: got %q, want %q", *ptr, profName)
	}
}

// TestConnFailedNoModalWhenSPMInactive verifies that ConnFailed is a no-op
// (pendingModal stays nil) when spmProfile is empty at callback time.
func TestConnFailedNoModalWhenSPMInactive(t *testing.T) {
	cfg, profName := newRefusedProfile(t)
	u := ui.New()
	d := &Dispatcher{u: u, cfg: cfg, reg: NewRegistry(), dotReg: NewRegistry()}
	// spmProfile stays "" — SPM is not active.
	connectToProfile(d, profName)

	// Wait for the dial to fail and the goroutine to finish.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if d.conn != nil && !d.conn.IsConnecting() {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	// Give ConnFailed a moment to run.
	time.Sleep(20 * time.Millisecond)

	if d.pendingModal.Load() != nil {
		t.Error("pendingModal should NOT be set when SPM is inactive (spmProfile empty)")
	}
}
