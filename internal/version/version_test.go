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

func TestString(t *testing.T) {
	original := version.Version
	defer func() { version.Version = original }()

	tests := []struct {
		version string
		want    string
	}{
		{"dev", "dev"},           // non-numeric identifier unchanged
		{"v1.2.3", "v1.2.3"},    // already has "v" prefix — unchanged
		{"1.2.3", "v1.2.3"},     // bare semver gets "v" prefix
		{"1.0.0-rc1", "v1.0.0-rc1"}, // pre-release semver gets "v" prefix
	}
	for _, tt := range tests {
		version.Version = tt.version
		got := version.String()
		if got != tt.want {
			t.Errorf("String() with Version=%q = %q, want %q", tt.version, got, tt.want)
		}
	}
}
