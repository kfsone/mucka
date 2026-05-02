// Package version defines the application version constants.
package version

// Version is set at build time via -ldflags, defaulting to "dev".
// Recommended build command:
//
//	go build -ldflags "-X github.com/kfsone/mucka/internal/version.Version=$(git describe --tags --always)" ./cmd/mucka
var Version = "dev"

const AppName = "mucka"
