// Package config loads and applies mucka's INI configuration.
package config

import (
	"fmt"
	"os"
	"path/filepath"

	"gopkg.in/ini.v1"
)

// General holds application-wide settings.
type General struct {
	FontName string `ini:"font-name"` // default "Go Mono"
	FontSize int    `ini:"font-size"` // default 14
	LogDir   string `ini:"log-dir"`
	Width    int    `ini:"width"`   // default 80
	Height   int    `ini:"height"`  // default 40
	History  int    `ini:"history"` // default 2000
	LogFileT string `ini:"log-file-t"`
	LogFmt   string `ini:"log-fmt"`
}

// FKeySet holds the 12 function key bindings for one modifier combination.
type FKeySet struct {
	F1  string `ini:"f1"`
	F2  string `ini:"f2"`
	F3  string `ini:"f3"`
	F4  string `ini:"f4"`
	F5  string `ini:"f5"`
	F6  string `ini:"f6"`
	F7  string `ini:"f7"`
	F8  string `ini:"f8"`
	F9  string `ini:"f9"`
	F10 string `ini:"f10"`
	F11 string `ini:"f11"`
	F12 string `ini:"f12"`
}

// Get returns the binding at index 1-12.
func (s *FKeySet) Get(i int) string {
	switch i {
	case 1:
		return s.F1
	case 2:
		return s.F2
	case 3:
		return s.F3
	case 4:
		return s.F4
	case 5:
		return s.F5
	case 6:
		return s.F6
	case 7:
		return s.F7
	case 8:
		return s.F8
	case 9:
		return s.F9
	case 10:
		return s.F10
	case 11:
		return s.F11
	case 12:
		return s.F12
	default:
		return ""
	}
}

// Set updates the binding at index 1-12.
func (s *FKeySet) Set(i int, v string) {
	switch i {
	case 1:
		s.F1 = v
	case 2:
		s.F2 = v
	case 3:
		s.F3 = v
	case 4:
		s.F4 = v
	case 5:
		s.F5 = v
	case 6:
		s.F6 = v
	case 7:
		s.F7 = v
	case 8:
		s.F8 = v
	case 9:
		s.F9 = v
	case 10:
		s.F10 = v
	case 11:
		s.F11 = v
	case 12:
		s.F12 = v
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
	var n int
	if _, err := fmt.Sscanf(key, "F%d", &n); err != nil {
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
}

// Config is the top-level configuration object.
type Config struct {
	General General
	FKeys   FKeyConfig
	Servers map[string]ServerProfile
}

// applyDefaults fills in zero-value fields with their defaults.
func applyDefaults(cfg *Config) {
	if cfg.General.FontName == "" {
		cfg.General.FontName = "Go Mono"
	}
	if cfg.General.FontSize == 0 {
		cfg.General.FontSize = 13
	}
	if cfg.General.Width == 0 {
		cfg.General.Width = 80
	}
	if cfg.General.Height == 0 {
		cfg.General.Height = 40
	}
	if cfg.General.History == 0 {
		cfg.General.History = 2000
	}
}

// Path returns the path to the mucka config file (%USERPROFILE%\mucka.ini).
func Path() string {
	return filepath.Join(os.Getenv("USERPROFILE"), "mucka.ini")
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
			if err := sec.MapTo(&cfg.FKeys.None); err != nil {
				return nil, fmt.Errorf("fkeys.none section: %w", err)
			}
		case "fkeys.shift":
			if err := sec.MapTo(&cfg.FKeys.Shift); err != nil {
				return nil, fmt.Errorf("fkeys.shift section: %w", err)
			}
		case "fkeys.ctrl":
			if err := sec.MapTo(&cfg.FKeys.Ctrl); err != nil {
				return nil, fmt.Errorf("fkeys.ctrl section: %w", err)
			}
		default:
			var sp ServerProfile
			if err := sec.MapTo(&sp); err != nil {
				return nil, fmt.Errorf("server profile %q: %w", name, err)
			}
			cfg.Servers[name] = sp
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
