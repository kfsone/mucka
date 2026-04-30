package fes

import (
	"math"
	"testing"
)

func TestFormatInt(t *testing.T) {
	cases := []struct {
		n    int64
		want string
	}{
		{0, "0"},
		{1, "1"},
		{999, "999"},
		{1000, "1,000"},
		{5184, "5,184"},
		{12345, "12,345"},
		{100000, "100,000"},
		{1000000, "1,000,000"},
		{-1234, "-1,234"},
		{-1000000, "-1,000,000"},
		// Boundary: largest and smallest int64 values must not panic.
		{math.MaxInt64, "9,223,372,036,854,775,807"},
		{math.MinInt64, "-9,223,372,036,854,775,808"},
	}
	for _, tc := range cases {
		got := FormatInt(tc.n)
		if got != tc.want {
			t.Errorf("FormatInt(%d) = %q, want %q", tc.n, got, tc.want)
		}
	}
}
