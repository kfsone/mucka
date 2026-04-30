package commands

import (
	"testing"
)

func TestPaginateLines(t *testing.T) {
	lines := make([]string, 25)
	for i := range lines {
		lines[i] = "line"
	}
	pages := paginateLines(lines, 10)
	if len(pages) != 3 {
		t.Fatalf("expected 3 pages, got %d", len(pages))
	}
	if len(pages[0]) != 10 {
		t.Errorf("page 0: expected 10 lines, got %d", len(pages[0]))
	}
	if len(pages[1]) != 10 {
		t.Errorf("page 1: expected 10 lines, got %d", len(pages[1]))
	}
	if len(pages[2]) != 5 {
		t.Errorf("page 2: expected 5 lines, got %d", len(pages[2]))
	}
}

func TestPaginateLinesEmpty(t *testing.T) {
	pages := paginateLines(nil, 10)
	if len(pages) != 0 {
		t.Errorf("expected empty pages, got %d", len(pages))
	}
	pages = paginateLines([]string{}, 10)
	if len(pages) != 0 {
		t.Errorf("expected empty pages for empty slice, got %d", len(pages))
	}
}

func TestPaginateLinesExact(t *testing.T) {
	lines := make([]string, 20)
	pages := paginateLines(lines, 20)
	if len(pages) != 1 {
		t.Fatalf("expected 1 page, got %d", len(pages))
	}
	if len(pages[0]) != 20 {
		t.Errorf("expected 20 lines, got %d", len(pages[0]))
	}
}

func TestPaginateLinesPartial(t *testing.T) {
	lines := make([]string, 7)
	pages := paginateLines(lines, 5)
	if len(pages) != 2 {
		t.Fatalf("expected 2 pages, got %d", len(pages))
	}
	if len(pages[0]) != 5 {
		t.Errorf("page 0: expected 5 lines, got %d", len(pages[0]))
	}
	if len(pages[1]) != 2 {
		t.Errorf("page 1: expected 2 lines, got %d", len(pages[1]))
	}
}

func TestPaginateLinesSingleLine(t *testing.T) {
	pages := paginateLines([]string{"only"}, 10)
	if len(pages) != 1 {
		t.Fatalf("expected 1 page, got %d", len(pages))
	}
	if len(pages[0]) != 1 || pages[0][0] != "only" {
		t.Errorf("unexpected page contents: %v", pages[0])
	}
}

// TestPaginateLinesExactlyPageSize verifies that lines == pageSize produces exactly 1 page.
func TestPaginateLinesExactlyPageSize(t *testing.T) {
	lines := []string{"a", "b", "c", "d", "e"}
	pages := paginateLines(lines, 5)
	if len(pages) != 1 {
		t.Fatalf("expected 1 page, got %d", len(pages))
	}
	if len(pages[0]) != 5 {
		t.Errorf("expected 5 lines in page 0, got %d", len(pages[0]))
	}
}
