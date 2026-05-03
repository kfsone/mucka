// Package config loads and applies mucka's INI configuration.
package config

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"gopkg.in/ini.v1"
)

// ProfilePrefix is the required prefix for server profile sections in the INI.
// e.g. [profile.mud2-uk] defines a profile named "profile.mud2-uk".
const ProfilePrefix = "profile."

// Default values for General settings.
const (
	DefaultFontName = "Go Mono"
	DefaultFontSize = 13
	DefaultWidth    = 80
	DefaultHeight   = 40
	DefaultHistory    = 2000
	DefaultScrollback = 5000
)

// General holds application-wide settings.
type General struct {
	FontName string `ini:"font-name"` // default DefaultFontName
	FontSize int    `ini:"font-size"` // default DefaultFontSize
	LogDir   string `ini:"log-dir"`
	Width    int    `ini:"width"`   // default DefaultWidth
	Height   int    `ini:"height"`  // default DefaultHeight
	History    int    `ini:"history"`    // default DefaultHistory
	Scrollback int    `ini:"scrollback"` // default DefaultScrollback
	LogFileT string `ini:"log-file-t"`
	LogFmt   string `ini:"log-fmt"`
}

// FKeySet holds the 12 function key bindings for one modifier combination.
type FKeySet [12]string

// Get returns the binding at index 1-12; returns "" for out-of-range values.
func (s *FKeySet) Get(i int) string {
	if i < 1 || i > 12 {
		return ""
	}
	return s[i-1]
}

// Set updates the binding at index 1-12.
func (s *FKeySet) Set(i int, v string) {
	if i >= 1 && i <= 12 {
		s[i-1] = v
	}
}

// FKeyConfig holds bindings for all three modifier combinations.
type FKeyConfig struct {
	None  FKeySet
	Shift FKeySet
	Ctrl  FKeySet
}

// GetCmd returns the binding for a given modifier name ("none"/"shift"/"ctrl") and key name ("F1"-"F12").
func (c *FKeyConfig) GetCmd(mod, key string) string {
	var set *FKeySet
	switch mod {
	case "none":
		set = &c.None
	case "shift":
		set = &c.Shift
	case "ctrl":
		set = &c.Ctrl
	default:
		return ""
	}
	if len(key) < 2 || key[0] != 'F' {
		return ""
	}
	n, err := strconv.Atoi(key[1:])
	if err != nil {
		return ""
	}
	return set.Get(n)
}

// SetByIndex returns a pointer to the FKeySet for index 0=None, 1=Shift, 2=Ctrl.
func (c *FKeyConfig) SetByIndex(i int) *FKeySet {
	switch i {
	case 0:
		return &c.None
	case 1:
		return &c.Shift
	case 2:
		return &c.Ctrl
	default:
		return nil
	}
}

// ServerProfile holds connection details for a single MUD server.
type ServerProfile struct {
	Host     string `ini:"host"`
	Port     int    `ini:"port"`
	Login    string `ini:"login"`
	Account  string `ini:"account"`
	Password string `ini:"password"`
	Width    int    `ini:"width"`  // terminal width; defaults to General.Width when 0
	Height   int    `ini:"height"` // terminal height; defaults to General.Height when 0
}

// Config is the top-level configuration object.
type Config struct {
	General       General
	FKeys         FKeyConfig
	Servers       map[string]ServerProfile
	ParseWarnings []string // deprecation/parse warnings collected during Load/parse
}

// LookupProfile finds a server profile by name, with a deprecated fallback for
// names without the "profile." prefix. Returns the profile, a bool indicating
// whether the deprecated fallback was used (caller should warn the user), and
// a bool indicating whether any profile was found at all.
func LookupProfile(servers map[string]ServerProfile, name string) (ServerProfile, bool, bool) {
	// Direct lookup first (handles both "profile.NAME" and old-style "NAME" keys).
	if sp, ok := servers[name]; ok {
		return sp, false, true
	}
	// Deprecated fallback: if name has no "profile." prefix, try prepending it.
	// This supports users who connect via ".connect mud2-uk" after updating their
	// INI to use the new "[profile.mud2-uk]" section format.
	if !strings.HasPrefix(name, ProfilePrefix) {
		if sp, ok := servers[ProfilePrefix+name]; ok {
			return sp, true, true
		}
	}
	return ServerProfile{}, false, false
}

// applyDefaults fills in zero-value fields with their defaults.
func applyDefaults(cfg *Config) {
	if cfg.General.FontName == "" {
		cfg.General.FontName = DefaultFontName
	}
	if cfg.General.FontSize == 0 {
		cfg.General.FontSize = DefaultFontSize
	}
	if cfg.General.Width == 0 {
		cfg.General.Width = DefaultWidth
	}
	if cfg.General.Height == 0 {
		cfg.General.Height = DefaultHeight
	}
	if cfg.General.History == 0 {
		cfg.General.History = DefaultHistory
	}
	if cfg.General.Scrollback == 0 {
		cfg.General.Scrollback = DefaultScrollback
	}
	// Propagate General terminal dimensions into any server profile that does
	// not override them explicitly.
	for name, sp := range cfg.Servers {
		if sp.Width == 0 {
			sp.Width = cfg.General.Width
		}
		if sp.Height == 0 {
			sp.Height = cfg.General.Height
		}
		cfg.Servers[name] = sp
	}
}

// Path returns the path to the mucka config file (%USERPROFILE%\mucka.ini).
func Path() string {
	return filepath.Join(os.Getenv("USERPROFILE"), "mucka.ini")
}

// Default returns a new Config populated entirely with default values.
func Default() *Config {
	cfg := &Config{}
	applyDefaults(cfg)
	return cfg
}

// Load reads %USERPROFILE%\mucka.ini and returns a populated Config.
// If the file does not exist, defaults are returned with no error.
func Load() (*Config, error) {
	path := Path()

	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		cfg := &Config{}
		applyDefaults(cfg)
		return cfg, nil
	}
	if err != nil {
		return nil, err
	}
	return parse(data)
}

// iniLoadOptions configures ini.v1 loading: preserve inline comments in passwords etc.
var iniLoadOptions = ini.LoadOptions{
	IgnoreInlineComment: true,
}

// parseFKeySection reads f1..f12 keys from an ini section into a FKeySet.
func parseFKeySection(sec *ini.Section, set *FKeySet) {
	for i := 1; i <= 12; i++ {
		set.Set(i, sec.Key(fmt.Sprintf("f%d", i)).Value())
	}
}

// parse decodes raw INI bytes into a Config, applying defaults afterwards.
// Sections [general], [fkeys.none], [fkeys.shift], [fkeys.ctrl] are mapped to
// their typed structs; all other sections become server profiles.
func parse(data []byte) (*Config, error) {
	cfg := &Config{
		Servers: make(map[string]ServerProfile),
	}

	if len(data) == 0 {
		applyDefaults(cfg)
		return cfg, nil
	}

	f, err := ini.LoadSources(iniLoadOptions, data)
	if err != nil {
		return nil, err
	}

	for _, sec := range f.Sections() {
		name := sec.Name()
		switch name {
		case ini.DefaultSection:
			// skip
		case "general":
			if err := sec.MapTo(&cfg.General); err != nil {
				return nil, fmt.Errorf("general section: %w", err)
			}
		case "fkeys.none":
			parseFKeySection(sec, &cfg.FKeys.None)
		case "fkeys.shift":
			parseFKeySection(sec, &cfg.FKeys.Shift)
		case "fkeys.ctrl":
			parseFKeySection(sec, &cfg.FKeys.Ctrl)
		default:
			var sp ServerProfile
			if err := sec.MapTo(&sp); err != nil {
				return nil, fmt.Errorf("server profile %q: %w", name, err)
			}
			if strings.HasPrefix(name, ProfilePrefix) {
				// New canonical format: [profile.NAME]
				cfg.Servers[name] = sp
			} else {
				// Deprecated format: [NAME] without the "profile." prefix.
				// Still loaded for backwards compatibility, but a warning is recorded.
				cfg.ParseWarnings = append(cfg.ParseWarnings,
					fmt.Sprintf("deprecated: section [%s] should be renamed to [%s%s]", name, ProfilePrefix, name))
				cfg.Servers[name] = sp
			}
		}
	}

	applyDefaults(cfg)
	return cfg, nil
}

// SaveFKeys writes the fkeys sections to the config file preserving existing
// content (comments, ordering, other sections). It loads the existing file via
// ini.v1 (which preserves comments), updates the three fkeys sub-sections, then
// atomically replaces the file.
func SaveFKeys(path string, fkeys FKeyConfig) error {
	var f *ini.File
	if data, err := os.ReadFile(path); err == nil {
		f, err = ini.LoadSources(iniLoadOptions, data)
		if err != nil {
			// Corrupt file — start fresh.
			f = ini.Empty()
		}
	} else {
		f = ini.Empty()
	}

	type sectionFKeys struct {
		name string
		set  *FKeySet
	}
	sections := []sectionFKeys{
		{"fkeys.none", &fkeys.None},
		{"fkeys.shift", &fkeys.Shift},
		{"fkeys.ctrl", &fkeys.Ctrl},
	}

	for _, sf := range sections {
		sec, err := f.NewSection(sf.name)
		if err != nil {
			// Section already exists — retrieve it.
			sec = f.Section(sf.name)
		}
		for i := 1; i <= 12; i++ {
			key := fmt.Sprintf("f%d", i)
			sec.Key(key).SetValue(sf.set.Get(i))
		}
	}

	tmp := path + ".tmp"
	if err := f.SaveTo(tmp); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}
