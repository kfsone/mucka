package fes

import "strconv"

// FormatInt formats n with comma separators (e.g. 5184 → "5,184").
// Handles zero, negative values, and math.MinInt64 correctly.
func FormatInt(n int64) string {
	if n < 0 {
		// uint64(-n) is safe even for math.MinInt64: the int64 negation
		// overflows but the bit-preserving uint64 conversion yields 2^63,
		// which is the correct absolute value.
		return "-" + formatUint(uint64(-n))
	}
	return formatUint(uint64(n))
}

// formatUint inserts comma thousand-separators into the decimal representation of u.
func formatUint(u uint64) string {
	s := strconv.FormatUint(u, 10)
	if len(s) <= 3 {
		return s
	}
	// Insert a comma every 3 digits from the right.
	rem := len(s) % 3
	if rem == 0 {
		rem = 3
	}
	result := make([]byte, 0, len(s)+(len(s)-1)/3)
	result = append(result, s[:rem]...)
	for i := rem; i < len(s); i += 3 {
		result = append(result, ',')
		result = append(result, s[i:i+3]...)
	}
	return string(result)
}
