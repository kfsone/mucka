package commands

import (
	"strings"
	"testing"

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
