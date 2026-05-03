package ui

import "testing"

func TestSplitPhrases(t *testing.T) {
	tests := []struct {
		input string
		want  []string
	}{
		{"", []string{""}},
		{"north", []string{"north"}},
		{"north,south", []string{"north", "south"}},
		{"smile,k dwarf with dagger", []string{"smile", "k dwarf with dagger"}},
		{`say "hello, world"`, []string{`say "hello, world"`}},
		{`a,"b,c",d`, []string{"a", `"b,c"`, "d"}},
		{"a,b,c", []string{"a", "b", "c"}},
	}
	for _, tc := range tests {
		got := splitPhrases(tc.input)
		if len(got) != len(tc.want) {
			t.Errorf("splitPhrases(%q): got %v, want %v", tc.input, got, tc.want)
			continue
		}
		for i := range got {
			if got[i] != tc.want[i] {
				t.Errorf("splitPhrases(%q)[%d] = %q, want %q", tc.input, i, got[i], tc.want[i])
			}
		}
	}
}

func TestLastPhrase(t *testing.T) {
	tests := []struct {
		input string
		want  string
	}{
		{"", ""},
		{"north", "north"},
		{"north,south", "south"},
		{"smile,k dwarf with dagger", "k dwarf with dagger"},
		{`say "hello, world"`, `say "hello, world"`},
		{`a,"b,c",d`, "d"},
	}
	for _, tc := range tests {
		got := lastPhrase(tc.input)
		if got != tc.want {
			t.Errorf("lastPhrase(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}

func TestExpandMacros(t *testing.T) {
	tests := []struct {
		name     string
		line     string
		prev     string
		prevPrev string
		want     string
	}{
		{
			name: "no macros unchanged",
			line: "north", prev: "south", prevPrev: "east",
			want: "north",
		},
		{
			name: "{!!} expands to prev",
			line: "ne,{!!}", prev: "k dwarf with dagger", prevPrev: "",
			want: "ne,k dwarf with dagger",
		},
		{
			name: "{!$} expands to last phrase of prev",
			line: "ne,{!$}", prev: "smile,k dwarf with dagger", prevPrev: "",
			want: "ne,k dwarf with dagger",
		},
		{
			name: "{!-} expands to prevPrev",
			line: "{!-}", prev: "ne", prevPrev: "smile,k dwarf with dagger",
			want: "smile,k dwarf with dagger",
		},
		{
			name: "{!-$} expands to last phrase of prevPrev",
			line: "{!-$}", prev: "ne", prevPrev: "smile,k dwarf with dagger",
			want: "k dwarf with dagger",
		},
		{
			name: "empty prev gives empty expansion for {!!}",
			line: "go,{!!}", prev: "", prevPrev: "",
			want: "go,",
		},
		{
			name: "multiple macros in one line",
			line: "{!!},{!!}", prev: "ne", prevPrev: "",
			want: "ne,ne",
		},
		{
			name: "{!-$} not confused with {!-}",
			line: "{!-$} and {!-}", prev: "ne", prevPrev: "smile,k orc",
			want: "k orc and smile,k orc",
		},
		{
			name: "no recursive expansion",
			line: "{!!}", prev: "{!!}", prevPrev: "old",
			want: "{!!}",
		},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := expandMacros(tc.line, tc.prev, tc.prevPrev)
			if got != tc.want {
				t.Errorf("expandMacros(%q, %q, %q) = %q, want %q",
					tc.line, tc.prev, tc.prevPrev, got, tc.want)
			}
		})
	}
}

func TestInputLine_doSubmit_macroHistory(t *testing.T) {
	il := NewInputLine()

	// First submit: "k dwarf with dagger"
	il.doSubmit("k dwarf with dagger")
	if il.SubmitText != "k dwarf with dagger" {
		t.Errorf("first submit SubmitText = %q", il.SubmitText)
	}
	if il.prevLine != "k dwarf with dagger" {
		t.Errorf("prevLine after first submit = %q", il.prevLine)
	}
	if il.prevPrevLine != "" {
		t.Errorf("prevPrevLine after first submit = %q", il.prevPrevLine)
	}

	// Second submit: "smile,k dwarf with dagger"
	il.doSubmit("smile,k dwarf with dagger")
	if il.prevLine != "smile,k dwarf with dagger" {
		t.Errorf("prevLine after second submit = %q", il.prevLine)
	}
	if il.prevPrevLine != "k dwarf with dagger" {
		t.Errorf("prevPrevLine after second submit = %q", il.prevPrevLine)
	}

	// Third submit: "ne" — sets up {!!} = "ne", {!-} = "smile,k dwarf with dagger"
	il.doSubmit("ne")

	// Now test macro expansion in a fourth submit.
	il.doSubmit("{!!},{!!}")
	if il.SubmitText != "ne,ne" {
		t.Errorf("{!!},{!!} SubmitText = %q, want %q", il.SubmitText, "ne,ne")
	}
	// Raw text stored in prevLine (not expanded)
	if il.prevLine != "{!!},{!!}" {
		t.Errorf("prevLine after macro submit = %q, want raw %q", il.prevLine, "{!!},{!!}")
	}

	// {!-} should still be "ne" (the previous prevLine before the macro submit)
	il.doSubmit("{!-}")
	if il.SubmitText != "ne" {
		t.Errorf("{!-} SubmitText = %q, want %q", il.SubmitText, "ne")
	}
}

func TestInputLine_doSubmit_macroExpandedTextSent(t *testing.T) {
	il := NewInputLine()
	il.doSubmit("k dwarf with dagger")
	il.doSubmit("ne,{!!}")
	if il.SubmitText != "ne,k dwarf with dagger" {
		t.Errorf("SubmitText = %q, want %q", il.SubmitText, "ne,k dwarf with dagger")
	}
}

func TestInputLine_doSubmit_rawStoredInHistory(t *testing.T) {
	il := NewInputLine()
	il.doSubmit("k orc")
	il.doSubmit("ne,{!!}")
	// The raw text (not expanded) should be at the tail of history.
	if il.history[len(il.history)-1] != "ne,{!!}" {
		t.Errorf("history tail = %q, want %q", il.history[len(il.history)-1], "ne,{!!}")
	}
}

func TestSanitizeClipboardText(t *testing.T) {
	tests := []struct {
		name  string
		input string
		want  string
	}{
		{"plain text unchanged", "hello world", "hello world"},
		{"truncate at newline", "line1\nline2", "line1"},
		{"truncate at carriage return", "line1\rline2", "line1"},
		{"truncate at CRLF", "line1\r\nline2", "line1"},
		{"truncate at first of multiple newlines", "a\nb\nc", "a"},
		{"empty string", "", ""},
		{"only newlines", "\r\n\r\n", ""},
		{"truncate mixed content", "kill\r\norc\nquickly", "kill"},
		// Unicode/non-ASCII must pass through unchanged.
		{"unicode passthrough", "café\nmüsli\r火", "café"},
		// Tabs and spaces are whitespace but must NOT be stripped.
		{"tabs and spaces preserved", "kill \t orc", "kill \t orc"},
		// Truncate at first newline; content before it (including spaces) is kept.
		{"long string truncate at first newline", "abcdefghij\nklmnopqrst\ruv wx\r\nyz0123456789", "abcdefghij"},
		// Leading newline truncates to empty string immediately.
		{"leading newline yields empty", "\nhello", ""},
		// Single word no special chars.
		{"single word no newline", "hello", "hello"},
		// Trailing CR only.
		{"trailing CR only", "hello\r", "hello"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := sanitizeClipboardText(tc.input)
			if got != tc.want {
				t.Errorf("sanitizeClipboardText(%q) = %q; want %q", tc.input, got, tc.want)
			}
		})
	}
}

func TestInputLine_FKeyProvider_nil(t *testing.T) {
	il := NewInputLine()
	// FKeyProvider is nil by default; no panic expected.
	if il.FKeyProvider != nil {
		t.Error("FKeyProvider should be nil by default")
	}
}

func TestInputLine_FKeyProvider_dispatch(t *testing.T) {
	il := NewInputLine()
	il.FKeyProvider = func(mod, key string) string {
		switch {
		case mod == "none" && key == "F1":
			return "say hello"
		case mod == "shift" && key == "F3":
			return "north"
		case mod == "ctrl" && key == "F12":
			return "quit"
		}
		return ""
	}

	cases := []struct {
		mod, key, want string
	}{
		{"none", "F1", "say hello"},
		{"shift", "F3", "north"},
		{"ctrl", "F12", "quit"},
		{"none", "F2", ""},    // unbound key → no submit
		{"alt", "F1", ""},     // unsupported modifier → no submit
	}
	for _, tc := range cases {
		il.Submitted = false
		il.SubmitText = ""
		// Simulate the F-key handler logic from Layout.
		if cmd := il.FKeyProvider(tc.mod, tc.key); cmd != "" {
			il.SubmitText = cmd
			il.Submitted = true
			il.everUsed = true
			il.appendHistory(cmd)
			il.editor.SetText("")
		}
		if tc.want == "" {
			if il.Submitted {
				t.Errorf("FKeyProvider(%q,%q): unexpected submit", tc.mod, tc.key)
			}
		} else {
			if !il.Submitted {
				t.Errorf("FKeyProvider(%q,%q): expected submit", tc.mod, tc.key)
			}
			if il.SubmitText != tc.want {
				t.Errorf("FKeyProvider(%q,%q): SubmitText = %q, want %q", tc.mod, tc.key, il.SubmitText, tc.want)
			}
		}
	}
}

func TestInputLine_FKeyProvider_emptyReturn_noSubmit(t *testing.T) {
	il := NewInputLine()
	il.FKeyProvider = func(mod, key string) string { return "" }
	// Empty return means no binding — should not submit.
	if cmd := il.FKeyProvider("none", "F5"); cmd != "" {
		t.Errorf("expected empty cmd, got %q", cmd)
	}
	if il.Submitted {
		t.Error("Submitted must remain false when FKeyProvider returns empty string")
	}
}

func TestInputLine_DreamWordProvider_nil(t *testing.T) {
	il := NewInputLine()
	// DreamWordProvider is nil by default; no panic expected.
	if il.DreamWordProvider != nil {
		t.Error("DreamWordProvider should be nil by default")
	}
}

func TestInputLine_DreamWordProvider_emptyWord(t *testing.T) {
	il := NewInputLine()
	called := false
	il.DreamWordProvider = func() string {
		called = true
		return ""
	}
	// Simulate the Ctrl-D handler logic: provider returns "" → no submit
	if il.DreamWordProvider != nil {
		if word := il.DreamWordProvider(); word != "" {
			il.SubmitText = `say "` + word + `"`
			il.Submitted = true
		}
	}
	if !called {
		t.Error("DreamWordProvider was not called")
	}
	if il.Submitted {
		t.Error("Submitted should be false when DreamWordProvider returns empty string")
	}
}

func TestInputLine_DreamWordProvider_withWord(t *testing.T) {
	il := NewInputLine()
	il.DreamWordProvider = func() string { return "frog" }

	// Simulate the Ctrl-D handler logic directly (matches Layout implementation).
	if il.DreamWordProvider != nil {
		if word := il.DreamWordProvider(); word != "" {
			cmd := `say "` + word + `"`
			il.SubmitText = cmd
			il.Submitted = true
			il.everUsed = true
			il.appendHistory(cmd)
			il.editor.SetText("")
		}
	}

	if !il.Submitted {
		t.Error("Submitted should be true after Ctrl-D with non-empty word")
	}
	if il.SubmitText != `say "frog"` {
		t.Errorf("SubmitText = %q, want %q", il.SubmitText, `say "frog"`)
	}
	if !il.everUsed {
		t.Error("everUsed should be true after Ctrl-D submit")
	}
	// History should contain the command.
	if len(il.history) == 0 || il.history[len(il.history)-1] != `say "frog"` {
		t.Errorf("history tail = %v, want last entry %q", il.history, `say "frog"`)
	}
	// Editor should be cleared.
	if il.editor.Text() != "" {
		t.Errorf("editor text = %q, want empty after Ctrl-D submit", il.editor.Text())
	}
}
