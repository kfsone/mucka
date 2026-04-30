package commands

import (
	"reflect"
	"testing"
)

func TestTokeniseDollarCommand(t *testing.T) {
	cmd := Tokenise("$stream foo.txt")
	if cmd.Type != Dollar {
		t.Errorf("Type: got %v, want Dollar", cmd.Type)
	}
	if cmd.Name != "$stream" {
		t.Errorf("Name: got %q, want %q", cmd.Name, "$stream")
	}
	if !reflect.DeepEqual(cmd.Args, []string{"foo.txt"}) {
		t.Errorf("Args: got %v, want [foo.txt]", cmd.Args)
	}
}

func TestTokeniseDotCommand(t *testing.T) {
	cmd := Tokenise(".quit")
	if cmd.Type != Dot {
		t.Errorf("Type: got %v, want Dot", cmd.Type)
	}
	if cmd.Name != ".quit" {
		t.Errorf("Name: got %q", cmd.Name)
	}
	if len(cmd.Args) != 0 {
		t.Errorf("Args: expected empty, got %v", cmd.Args)
	}
}

func TestTokenisePlainText(t *testing.T) {
	cmd := Tokenise("hello world")
	if cmd.Type != Plain {
		t.Errorf("Type: got %v, want Plain", cmd.Type)
	}
	if cmd.Name != "hello" {
		t.Errorf("Name: got %q", cmd.Name)
	}
	if !reflect.DeepEqual(cmd.Args, []string{"world"}) {
		t.Errorf("Args: got %v", cmd.Args)
	}
}

func TestTokeniseEmpty(t *testing.T) {
	cmd := Tokenise("")
	if cmd.Type != Plain {
		t.Errorf("Type: got %v, want Plain", cmd.Type)
	}
	if cmd.Name != "" {
		t.Errorf("Name: expected empty, got %q", cmd.Name)
	}
	if len(cmd.Args) != 0 {
		t.Errorf("Args: expected empty, got %v", cmd.Args)
	}
}

func TestTokeniseWhitespace(t *testing.T) {
	cmd := Tokenise("   ")
	if cmd.Type != Plain {
		t.Errorf("Type: got %v, want Plain", cmd.Type)
	}
	if cmd.Name != "" {
		t.Errorf("Name: expected empty, got %q", cmd.Name)
	}
}

func TestTokeniseDollarMultipleArgs(t *testing.T) {
	cmd := Tokenise("$load file1.txt file2.txt")
	if cmd.Type != Dollar {
		t.Errorf("Type: want Dollar")
	}
	if cmd.Name != "$load" {
		t.Errorf("Name: got %q", cmd.Name)
	}
	if !reflect.DeepEqual(cmd.Args, []string{"file1.txt", "file2.txt"}) {
		t.Errorf("Args: got %v", cmd.Args)
	}
}

func TestTokeniseDotWithArgs(t *testing.T) {
	cmd := Tokenise(".say hello there")
	if cmd.Type != Dot {
		t.Errorf("Type: want Dot")
	}
	if !reflect.DeepEqual(cmd.Args, []string{"hello", "there"}) {
		t.Errorf("Args: got %v", cmd.Args)
	}
}

func TestRegistryDispatch(t *testing.T) {
	r := NewRegistry()
	called := false
	r.Register("$test", "test command", func(args []string) {
		called = true
	})

	cmd := Tokenise("$test arg1")
	found := r.Dispatch(cmd)
	if !found {
		t.Error("expected handler to be found")
	}
	if !called {
		t.Error("handler was not called")
	}
}

func TestRegistryDispatchUnknown(t *testing.T) {
	r := NewRegistry()
	cmd := Tokenise("$unknown")
	found := r.Dispatch(cmd)
	if found {
		t.Error("expected no handler for unknown command")
	}
}

// TestTokeniseLeadingAndTrailingWhitespace verifies Fields trims surrounding spaces.
func TestTokeniseLeadingAndTrailingWhitespace(t *testing.T) {
	cmd := Tokenise("  $cmd arg  ")
	if cmd.Type != Dollar {
		t.Errorf("Type: want Dollar, got %v", cmd.Type)
	}
	if cmd.Name != "$cmd" {
		t.Errorf("Name: got %q", cmd.Name)
	}
	if len(cmd.Args) != 1 || cmd.Args[0] != "arg" {
		t.Errorf("Args: got %v", cmd.Args)
	}
}

// TestTokeniseMultiSpaceBetweenArgs verifies extra whitespace between args is collapsed.
func TestTokeniseMultiSpaceBetweenArgs(t *testing.T) {
	cmd := Tokenise("$cmd  arg1   arg2")
	if len(cmd.Args) != 2 {
		t.Fatalf("expected 2 args, got %v", cmd.Args)
	}
	if cmd.Args[0] != "arg1" || cmd.Args[1] != "arg2" {
		t.Errorf("unexpected args: %v", cmd.Args)
	}
}

// TestTokeniseBareDollar verifies "$" alone is Dollar type with empty args.
func TestTokeniseBarePrefix(t *testing.T) {
	for _, input := range []struct {
		s    string
		want CommandType
	}{
		{"$", Dollar},
		{".", Dot},
	} {
		cmd := Tokenise(input.s)
		if cmd.Type != input.want {
			t.Errorf("%q: Type got %v, want %v", input.s, cmd.Type, input.want)
		}
		if cmd.Name != input.s {
			t.Errorf("%q: Name got %q", input.s, cmd.Name)
		}
		if len(cmd.Args) != 0 {
			t.Errorf("%q: expected no args, got %v", input.s, cmd.Args)
		}
	}
}

// TestTokeniseTabWhitespace verifies tab characters are treated as whitespace.
func TestTokeniseTabWhitespace(t *testing.T) {
	cmd := Tokenise("$cmd\targ1\targ2")
	if cmd.Type != Dollar {
		t.Errorf("Type: want Dollar, got %v", cmd.Type)
	}
	if len(cmd.Args) != 2 {
		t.Fatalf("expected 2 args, got %v", cmd.Args)
	}
}

// TestTokeniseSingleWord verifies a single plain word has no args.
func TestTokeniseSingleWord(t *testing.T) {
	cmd := Tokenise("hello")
	if cmd.Type != Plain {
		t.Errorf("Type: want Plain")
	}
	if cmd.Name != "hello" {
		t.Errorf("Name: got %q", cmd.Name)
	}
	if len(cmd.Args) != 0 {
		t.Errorf("expected no args, got %v", cmd.Args)
	}
}

// TestRegistryEntriesEmpty verifies Entries returns nil/empty for a fresh registry.
func TestRegistryEntriesEmpty(t *testing.T) {
	r := NewRegistry()
	entries := r.Entries()
	if len(entries) != 0 {
		t.Errorf("expected 0 entries, got %d: %v", len(entries), entries)
	}
}

// TestRegistryEntriesSingle verifies Entries returns the one registered command.
func TestRegistryEntriesSingle(t *testing.T) {
	r := NewRegistry()
	r.Register("$foo", "foo command", func(args []string) {})
	entries := r.Entries()
	if len(entries) != 1 {
		t.Fatalf("expected 1 entry, got %d", len(entries))
	}
	if entries[0].Name != "$foo" {
		t.Errorf("Name: got %q, want %q", entries[0].Name, "$foo")
	}
	if entries[0].Desc != "foo command" {
		t.Errorf("Desc: got %q, want %q", entries[0].Desc, "foo command")
	}
}

// TestRegistryEntriesSorted verifies Entries returns commands sorted by name.
func TestRegistryEntriesSorted(t *testing.T) {
	r := NewRegistry()
	r.Register("$zzz", "last", func(args []string) {})
	r.Register("$aaa", "first", func(args []string) {})
	r.Register("$mmm", "middle", func(args []string) {})
	entries := r.Entries()
	if len(entries) != 3 {
		t.Fatalf("expected 3 entries, got %d", len(entries))
	}
	want := []string{"$aaa", "$mmm", "$zzz"}
	for i, e := range entries {
		if e.Name != want[i] {
			t.Errorf("entries[%d].Name = %q, want %q", i, e.Name, want[i])
		}
	}
}

// TestRegistryEntriesDescPreserved verifies each entry's Desc round-trips correctly.
func TestRegistryEntriesDescPreserved(t *testing.T) {
	r := NewRegistry()
	r.Register("$a", "desc-a", func(args []string) {})
	r.Register("$b", "desc-b", func(args []string) {})
	entries := r.Entries()
	want := map[string]string{"$a": "desc-a", "$b": "desc-b"}
	for _, e := range entries {
		d, ok := want[e.Name]
		if !ok {
			t.Errorf("unexpected entry name %q", e.Name)
			continue
		}
		if e.Desc != d {
			t.Errorf("entry %q desc: got %q, want %q", e.Name, e.Desc, d)
		}
	}
}
