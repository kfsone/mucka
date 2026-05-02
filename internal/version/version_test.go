package version_test

import (
	"testing"

	"github.com/kfsone/mucka/internal/version"
)

// TestVersionIsVar confirms that Version is a settable variable (not a const),
// which is required for -ldflags injection at build time.
func TestVersionIsVar(t *testing.T) {
	original := version.Version
	defer func() { version.Version = original }()

	version.Version = "test-value"
	if version.Version != "test-value" {
		t.Errorf("Version = %q, want %q", version.Version, "test-value")
	}
}

func TestVersionDefault(t *testing.T) {
	if version.Version == "" {
		t.Error("Version must not be empty")
	}
}
