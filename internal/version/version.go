// Package version defines the application version constants.
package version

import "runtime/debug"

// Version is set at build time via -ldflags, defaulting to "dev".
// Recommended build command:
//
//	go build -ldflags "-X github.com/kfsone/mucka/internal/version.Version=$(git describe --tags --always)" ./cmd/mucka
var Version = "dev"

const AppName = "mucka"

// init() populates Version from module build info when -ldflags was not used.
// This covers binaries installed via "go install module@version", where Go
// embeds the module version automatically.
func init() {
	if Version != "dev" {
		return
	}
	if info, ok := debug.ReadBuildInfo(); ok {
		if v := info.Main.Version; v != "" && v != "(devel)" {
			Version = v
		}
	}
}

// String returns Version in a display-friendly format.
// Bare semver strings (starting with a digit) are prefixed with "v".
// Versions already starting with "v" or non-numeric identifiers (e.g. "dev")
// are returned unchanged.
func String() string {
	if len(Version) > 0 && Version[0] >= '0' && Version[0] <= '9' {
		return "v" + Version
	}
	return Version
}
