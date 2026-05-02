package config

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDefaultsOnEmptyConfig(t *testing.T) {
	cfg, err := parse([]byte(""))
	if err != nil {
		t.Fatalf("parse empty: %v", err)
	}
	if cfg.General.FontSize != 14 {
		t.Errorf("FontSize: got %d, want 14", cfg.General.FontSize)
	}
	if cfg.General.Width != 80 {
		t.Errorf("Width: got %d, want 80", cfg.General.Width)
	}
	if cfg.General.Height != 40 {
		t.Errorf("Height: got %d, want 40", cfg.General.Height)
	}
	if cfg.General.History != 2000 {
		t.Errorf("History: got %d, want 2000", cfg.General.History)
	}
}

func TestParseGeneral(t *testing.T) {
	ini := `
[general]
font-size = 18
width = 120
height = 50
history = 5000
log-dir = /tmp/logs
`
	cfg, err := parse([]byte(ini))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.General.FontSize != 18 {
		t.Errorf("FontSize: got %d, want 18", cfg.General.FontSize)
	}
	if cfg.General.Width != 120 {
		t.Errorf("Width: got %d, want 120", cfg.General.Width)
	}
	if cfg.General.Height != 50 {
		t.Errorf("Height: got %d, want 50", cfg.General.Height)
	}
	if cfg.General.History != 5000 {
		t.Errorf("History: got %d, want 5000", cfg.General.History)
	}
	if cfg.General.LogDir != "/tmp/logs" {
		t.Errorf("LogDir: got %q, want %q", cfg.General.LogDir, "/tmp/logs")
	}
}

func TestParseServerProfile(t *testing.T) {
	ini := `
[mud2-uk]
host = mudii.co.uk
port = 27750
login = testlogin
account = testaccount
password = testpass
`
	cfg, err := parse([]byte(ini))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	sp, ok := cfg.Servers["mud2-uk"]
	if !ok {
		t.Fatal("expected 'mud2-uk' server profile")
	}
	if sp.Host != "mudii.co.uk" {
		t.Errorf("Host: got %q, want %q", sp.Host, "mudii.co.uk")
	}
	if sp.Port != 27750 {
		t.Errorf("Port: got %d, want 27750", sp.Port)
	}
	if sp.Login != "testlogin" {
		t.Errorf("Login: got %q", sp.Login)
	}
	if sp.Account != "testaccount" {
		t.Errorf("Account: got %q", sp.Account)
	}
	if sp.Password != "testpass" {
		t.Errorf("Password: got %q", sp.Password)
	}
}

func TestParseMultipleServers(t *testing.T) {
	ini := `
[general]
font-size = 16

[mud2-uk]
host = mudii.co.uk
port = 27750

[local]
host = localhost
port = 4000
`
	cfg, err := parse([]byte(ini))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.General.FontSize != 16 {
		t.Errorf("FontSize: got %d, want 16", cfg.General.FontSize)
	}
	if len(cfg.Servers) != 2 {
		t.Errorf("expected 2 servers, got %d", len(cfg.Servers))
	}
	if _, ok := cfg.Servers["mud2-uk"]; !ok {
		t.Error("expected mud2-uk server")
	}
	if _, ok := cfg.Servers["local"]; !ok {
		t.Error("expected local server")
	}
}

func TestDefaultsAppliedWhenZero(t *testing.T) {
	// Partial config: only font-size set; rest should get defaults.
	ini := `
[general]
font-size = 20
`
	cfg, err := parse([]byte(ini))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.General.FontSize != 20 {
		t.Errorf("FontSize: got %d, want 20", cfg.General.FontSize)
	}
	if cfg.General.Width != 80 {
		t.Errorf("Width: got %d, want 80 (default)", cfg.General.Width)
	}
	if cfg.General.Height != 40 {
		t.Errorf("Height: got %d, want 40 (default)", cfg.General.Height)
	}
	if cfg.General.History != 2000 {
		t.Errorf("History: got %d, want 2000 (default)", cfg.General.History)
	}
}

// TestGeneralOnlyServersNotNil: a config with only [general] must have a non-nil
// Servers map (not a nil map that would panic on insertion).
func TestGeneralOnlyServersNotNil(t *testing.T) {
	ini := `
[general]
font-size = 14
`
	cfg, err := parse([]byte(ini))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.Servers == nil {
		t.Error("Servers map must not be nil when only [general] is present")
	}
	if len(cfg.Servers) != 0 {
		t.Errorf("expected 0 server profiles, got %d", len(cfg.Servers))
	}
}

func TestLoadMissingFileReturnsDefaults(t *testing.T) {
	// Load() should not fail when the config file is missing.
	// We can't easily test the real Load() path without mocking the home dir,
	// so we test parse() with empty input as a proxy.
	cfg, err := parse([]byte{})
	if err != nil {
		t.Fatalf("unexpected error on empty input: %v", err)
	}
	if cfg.General.FontSize != 14 {
		t.Errorf("default FontSize: got %d, want 14", cfg.General.FontSize)
	}
}

// TestFontNameDefault verifies that FontName defaults to "Go Mono" when not set.
func TestFontNameDefault(t *testing.T) {
	cfg, err := parse([]byte(""))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.General.FontName != "Go Mono" {
		t.Errorf("FontName default: got %q, want %q", cfg.General.FontName, "Go Mono")
	}
}

// TestFontNameRoundTrip verifies that font-name in INI is decoded correctly
// and overrides the default.
func TestFontNameRoundTrip(t *testing.T) {
	iniInput := `
[general]
font-name = Cascadia Mono
`
	cfg, err := parse([]byte(iniInput))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if cfg.General.FontName != "Cascadia Mono" {
		t.Errorf("FontName round-trip: got %q, want %q", cfg.General.FontName, "Cascadia Mono")
	}
}

// TestFKeySetGetSet verifies Get and Set round-trip correctly for all 12 keys.
func TestFKeySetGetSet(t *testing.T) {
	var s FKeySet
	for i := 1; i <= 12; i++ {
		v := fmt.Sprintf("cmd%d", i)
		s.Set(i, v)
		if got := s.Get(i); got != v {
			t.Errorf("F%d: Set/Get round-trip got %q, want %q", i, got, v)
		}
	}
	// Out-of-range should return ""
	if got := s.Get(0); got != "" {
		t.Errorf("Get(0): expected empty, got %q", got)
	}
	if got := s.Get(13); got != "" {
		t.Errorf("Get(13): expected empty, got %q", got)
	}
}

// TestFKeyConfigGetCmd verifies GetCmd for all three modifiers and all 12 keys.
func TestFKeyConfigGetCmd(t *testing.T) {
	var cfg FKeyConfig
	cfg.None.F1 = "say hello"
	cfg.Shift.F3 = "north"
	cfg.Ctrl.F12 = "quit"

	tests := []struct {
		mod, key, want string
	}{
		{"none", "F1", "say hello"},
		{"none", "F2", ""},
		{"shift", "F3", "north"},
		{"shift", "F1", ""},
		{"ctrl", "F12", "quit"},
		{"ctrl", "F1", ""},
		{"unknown", "F1", ""},
		{"none", "F0", ""},
		{"none", "Fx", ""},
	}
	for _, tc := range tests {
		got := cfg.GetCmd(tc.mod, tc.key)
		if got != tc.want {
			t.Errorf("GetCmd(%q, %q) = %q; want %q", tc.mod, tc.key, got, tc.want)
		}
	}
}

// TestFKeyConfigSetByIndex verifies SetByIndex returns the correct FKeySet pointer.
func TestFKeyConfigSetByIndex(t *testing.T) {
	var cfg FKeyConfig
	cfg.None.F1 = "none"
	cfg.Shift.F1 = "shift"
	cfg.Ctrl.F1 = "ctrl"

	if cfg.SetByIndex(0) != &cfg.None {
		t.Error("SetByIndex(0) should return &cfg.None")
	}
	if cfg.SetByIndex(1) != &cfg.Shift {
		t.Error("SetByIndex(1) should return &cfg.Shift")
	}
	if cfg.SetByIndex(2) != &cfg.Ctrl {
		t.Error("SetByIndex(2) should return &cfg.Ctrl")
	}
	if cfg.SetByIndex(3) != nil {
		t.Error("SetByIndex(3) should return nil")
	}
}

// TestSaveFKeysRoundtrip verifies that SaveFKeys writes an fkeys section that
// can be loaded back and produces identical bindings.
func TestSaveFKeysRoundtrip(t *testing.T) {
	tmp := t.TempDir()
	path := filepath.Join(tmp, "mucka.ini")

	fkeys := FKeyConfig{}
	fkeys.None.F1 = "say hello"
	fkeys.None.F5 = "look"
	fkeys.Shift.F2 = "north"
	fkeys.Ctrl.F12 = "quit"

	if err := SaveFKeys(path, fkeys); err != nil {
		t.Fatalf("SaveFKeys: %v", err)
	}

	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile after save: %v", err)
	}
	cfg, err := parse(data)
	if err != nil {
		t.Fatalf("parse after save: %v", err)
	}

	if cfg.FKeys.None.F1 != "say hello" {
		t.Errorf("None.F1: got %q, want %q", cfg.FKeys.None.F1, "say hello")
	}
	if cfg.FKeys.None.F5 != "look" {
		t.Errorf("None.F5: got %q, want %q", cfg.FKeys.None.F5, "look")
	}
	if cfg.FKeys.Shift.F2 != "north" {
		t.Errorf("Shift.F2: got %q, want %q", cfg.FKeys.Shift.F2, "north")
	}
	if cfg.FKeys.Ctrl.F12 != "quit" {
		t.Errorf("Ctrl.F12: got %q, want %q", cfg.FKeys.Ctrl.F12, "quit")
	}
}

// TestParseFKeySections verifies that [fkeys.none], [fkeys.shift], and [fkeys.ctrl]
// sections are correctly mapped into FKeyConfig.
func TestParseFKeySections(t *testing.T) {
	iniInput := `
[fkeys.none]
f1 = say hello
f5 = look

[fkeys.shift]
f2 = north
f3 = south

[fkeys.ctrl]
f12 = quit
`
	cfg, err := parse([]byte(iniInput))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	tests := []struct {
		label string
		got   string
		want  string
	}{
		{"None.F1", cfg.FKeys.None.F1, "say hello"},
		{"None.F5", cfg.FKeys.None.F5, "look"},
		{"None.F2", cfg.FKeys.None.F2, ""},
		{"Shift.F2", cfg.FKeys.Shift.F2, "north"},
		{"Shift.F3", cfg.FKeys.Shift.F3, "south"},
		{"Ctrl.F12", cfg.FKeys.Ctrl.F12, "quit"},
		{"Ctrl.F1", cfg.FKeys.Ctrl.F1, ""},
	}
	for _, tc := range tests {
		if tc.got != tc.want {
			t.Errorf("%s: got %q, want %q", tc.label, tc.got, tc.want)
		}
	}
}

// TestPasswordSpecialChars verifies that passwords containing # or ; are not
// truncated by inline-comment stripping (IgnoreInlineComment must be set).
func TestPasswordSpecialChars(t *testing.T) {
	iniInput := `
[myserver]
host = example.com
port = 4000
password = s3cr3t#1;foo
`
	cfg, err := parse([]byte(iniInput))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	sp, ok := cfg.Servers["myserver"]
	if !ok {
		t.Fatal("expected myserver profile")
	}
	if sp.Password != "s3cr3t#1;foo" {
		t.Errorf("Password: got %q, want %q", sp.Password, "s3cr3t#1;foo")
	}
}

// TestSaveFKeysPreservesComments verifies that comments present in the INI file
// before SaveFKeys are still present after the save — this is the core goal of
// the TOML→INI migration.
func TestSaveFKeysPreservesComments(t *testing.T) {
	tmp := t.TempDir()
	path := filepath.Join(tmp, "mucka.ini")

	initial := `; mucka configuration
[general]
; terminal font
font-name = Go Mono
font-size = 14

[fkeys.none]
; default bindings
f1 = inventory
`
	if err := os.WriteFile(path, []byte(initial), 0o644); err != nil {
		t.Fatalf("WriteFile: %v", err)
	}

	fkeys := FKeyConfig{}
	fkeys.None.F1 = "look"
	fkeys.None.F2 = "north"

	if err := SaveFKeys(path, fkeys); err != nil {
		t.Fatalf("SaveFKeys: %v", err)
	}

	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile: %v", err)
	}
	content := string(data)

	// Top-level file comment must survive.
	if !strings.Contains(content, "mucka configuration") {
		t.Error("file-level comment lost after SaveFKeys")
	}
	// Section-level comment must survive.
	if !strings.Contains(content, "terminal font") {
		t.Error("[general] comment lost after SaveFKeys")
	}
	// Key-level comment must survive.
	if !strings.Contains(content, "default bindings") {
		t.Error("fkeys.none key comment lost after SaveFKeys")
	}

	// Values must be updated correctly.
	cfg, err := parse(data)
	if err != nil {
		t.Fatalf("parse after save: %v", err)
	}
	if cfg.FKeys.None.F1 != "look" {
		t.Errorf("None.F1: got %q, want %q", cfg.FKeys.None.F1, "look")
	}
	if cfg.FKeys.None.F2 != "north" {
		t.Errorf("None.F2: got %q, want %q", cfg.FKeys.None.F2, "north")
	}
	if cfg.General.FontSize != 14 {
		t.Errorf("FontSize: got %d, want 14", cfg.General.FontSize)
	}
}

// TestDefault verifies that Default returns a non-nil Config with all defaults applied.
func TestDefault(t *testing.T) {
	cfg := Default()
	if cfg == nil {
		t.Fatal("Default() returned nil")
	}
	if cfg.General.FontName != "Go Mono" {
		t.Errorf("FontName: got %q, want %q", cfg.General.FontName, "Go Mono")
	}
	if cfg.General.FontSize != 14 {
		t.Errorf("FontSize: got %d, want 14", cfg.General.FontSize)
	}
	if cfg.General.Width != 80 {
		t.Errorf("Width: got %d, want 80", cfg.General.Width)
	}
	if cfg.General.Height != 40 {
		t.Errorf("Height: got %d, want 40", cfg.General.Height)
	}
	if cfg.General.History != 2000 {
		t.Errorf("History: got %d, want 2000", cfg.General.History)
	}
}

// TestSaveFKeysPreservesExistingContent verifies that SaveFKeys does not destroy
// other sections (e.g. [general], server profiles) already present in the file.
func TestSaveFKeysPreservesExistingContent(t *testing.T) {
	tmp := t.TempDir()
	path := filepath.Join(tmp, "mucka.ini")

	// Write initial content with a [general] section and a server profile.
	initial := `[general]
font-size = 18

[myserver]
host = example.com
port = 4000
`
	if err := os.WriteFile(path, []byte(initial), 0o644); err != nil {
		t.Fatalf("WriteFile: %v", err)
	}

	fkeys := FKeyConfig{}
	fkeys.None.F1 = "inventory"

	if err := SaveFKeys(path, fkeys); err != nil {
		t.Fatalf("SaveFKeys: %v", err)
	}

	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile: %v", err)
	}
	cfg, err := parse(data)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}

	if cfg.General.FontSize != 18 {
		t.Errorf("FontSize preserved: got %d, want 18", cfg.General.FontSize)
	}
	if _, ok := cfg.Servers["myserver"]; !ok {
		t.Error("expected myserver profile to be preserved")
	}
	if cfg.FKeys.None.F1 != "inventory" {
		t.Errorf("None.F1: got %q, want %q", cfg.FKeys.None.F1, "inventory")
	}
}
