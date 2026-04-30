package commands

import (
	"os"
	"path/filepath"
	"testing"
)

func TestReadFileLinesSkipsComments(t *testing.T) {
	lines, err := readFileLines(filepath.Join("testdata", "demo.txt"))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	for _, line := range lines {
		if len(line) > 0 && line[0] == '#' {
			t.Errorf("comment line was not skipped: %q", line)
		}
	}
	// demo.txt has 4 non-comment, non-empty lines
	if len(lines) != 4 {
		t.Errorf("expected 4 lines, got %d: %v", len(lines), lines)
	}
}

func TestReadFileLinesReturnsError(t *testing.T) {
	_, err := readFileLines(filepath.Join("testdata", "nonexistent_file_xyz.txt"))
	if err == nil {
		t.Error("expected error for nonexistent file, got nil")
	}
	if !os.IsNotExist(err) {
		t.Errorf("expected not-exist error, got: %v", err)
	}
}

func TestReadFileLinesEmpty(t *testing.T) {
	// Create a temporary empty file in testdata
	tmp := filepath.Join("testdata", "empty_test.txt")
	f, err := os.Create(tmp)
	if err != nil {
		t.Fatalf("could not create temp file: %v", err)
	}
	f.Close()
	defer os.Remove(tmp)

	lines, err := readFileLines(tmp)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(lines) != 0 {
		t.Errorf("expected empty slice, got %v", lines)
	}
}

func TestReadFileLinesAllComments(t *testing.T) {
	tmp := filepath.Join("testdata", "all_comments_test.txt")
	if err := os.WriteFile(tmp, []byte("# first comment\n# second comment\n"), 0644); err != nil {
		t.Fatalf("could not create file: %v", err)
	}
	defer os.Remove(tmp)

	lines, err := readFileLines(tmp)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(lines) != 0 {
		t.Errorf("expected no lines from all-comment file, got %v", lines)
	}
}

func TestReadFileLinesBlankAndComments(t *testing.T) {
	tmp := filepath.Join("testdata", "blank_comments_test.txt")
	content := "# header comment\n\nactual line\n\n# trailing comment\n"
	if err := os.WriteFile(tmp, []byte(content), 0644); err != nil {
		t.Fatalf("could not create file: %v", err)
	}
	defer os.Remove(tmp)

	lines, err := readFileLines(tmp)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	if lines[0] != "actual line" {
		t.Errorf("expected %q, got %q", "actual line", lines[0])
	}
}

func TestReadFileLinesTrailingNewline(t *testing.T) {
	tmp := filepath.Join("testdata", "trailing_newline_test.txt")
	if err := os.WriteFile(tmp, []byte("line1\nline2\n"), 0644); err != nil {
		t.Fatalf("could not create file: %v", err)
	}
	defer os.Remove(tmp)

	lines, err := readFileLines(tmp)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(lines) != 2 {
		t.Fatalf("expected 2 lines, got %d: %v", len(lines), lines)
	}
	if lines[0] != "line1" || lines[1] != "line2" {
		t.Errorf("unexpected lines: %v", lines)
	}
}
