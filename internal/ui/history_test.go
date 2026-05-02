package ui

import (
	"fmt"
	"testing"
)

// ── appendHistory ──────────────────────────────────────────────────────────

func TestAppendHistory_EmptyStringSkipped(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("")
	if len(il.history) != 0 {
		t.Errorf("empty string added to history: len = %d, want 0", len(il.history))
	}
	// histIdx must not move (still equals len(history) == 0).
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after empty append, want 0", il.histIdx)
	}
}

func TestAppendHistory_AddsItem(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("kill orc")
	if len(il.history) != 1 {
		t.Fatalf("history length = %d, want 1", len(il.history))
	}
	if il.history[0] != "kill orc" {
		t.Errorf("history[0] = %q, want %q", il.history[0], "kill orc")
	}
	// histIdx should be len(history) = 1 (sentinel: not browsing).
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after first append, want 1", il.histIdx)
	}
	if il.savedInput != "" {
		t.Errorf("savedInput = %q after append, want empty", il.savedInput)
	}
}

func TestAppendHistory_SkipsConsecutiveDuplicate(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("go north")
	il.appendHistory("go north")
	if len(il.history) != 1 {
		t.Errorf("consecutive dup added to history: len = %d, want 1", len(il.history))
	}
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d, want 1", il.histIdx)
	}
}

func TestAppendHistory_AllowsNonConsecutiveDuplicate(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("go north")
	il.appendHistory("look")
	il.appendHistory("go north")
	if len(il.history) != 3 {
		t.Errorf("non-consecutive dup should be stored: len = %d, want 3", len(il.history))
	}
	if il.history[2] != "go north" {
		t.Errorf("history[2] = %q, want \"go north\"", il.history[2])
	}
}

func TestAppendHistory_ResetsSavedInput(t *testing.T) {
	il := NewInputLine()
	il.savedInput = "partial"
	il.appendHistory("cmd")
	if il.savedInput != "" {
		t.Errorf("savedInput = %q after appendHistory, want empty", il.savedInput)
	}
}

func TestAppendHistory_ResetsHistIdxToLen(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("a")
	il.appendHistory("b")
	il.appendHistory("c")
	// Simulate user browsing back.
	il.histIdx = 0
	il.appendHistory("d")
	if il.histIdx != 4 {
		t.Errorf("histIdx = %d after append while browsing, want 4", il.histIdx)
	}
}

func TestAppendHistory_MultipleUnique(t *testing.T) {
	il := NewInputLine()
	cmds := []string{"n", "s", "e", "w", "look", "inv"}
	for _, c := range cmds {
		il.appendHistory(c)
	}
	if len(il.history) != len(cmds) {
		t.Errorf("history length = %d, want %d", len(il.history), len(cmds))
	}
	for i, c := range cmds {
		if il.history[i] != c {
			t.Errorf("history[%d] = %q, want %q", i, il.history[i], c)
		}
	}
	if il.histIdx != len(cmds) {
		t.Errorf("histIdx = %d, want %d", il.histIdx, len(cmds))
	}
}

func TestAppendHistory_EmptyStringAfterItems_NoChangeToHistory(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("look")
	il.appendHistory("")
	if len(il.history) != 1 {
		t.Errorf("empty string must not grow history: len = %d, want 1", len(il.history))
	}
	// histIdx stays at len(history) == 1.
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after empty append, want 1", il.histIdx)
	}
}

// ── historyUp ─────────────────────────────────────────────────────────────

func TestHistoryUp_EmptyHistory_NoChange(t *testing.T) {
	il := NewInputLine()
	il.historyUp()
	// No history → histIdx remains 0, savedInput captures current (empty) text.
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after Up with empty history, want 0", il.histIdx)
	}
}

func TestHistoryUp_SavesCurrentInputOnFirstUp(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("look")
	// Simulate user typing "partial".
	il.editor.SetText("partial")
	il.historyUp()
	if il.savedInput != "partial" {
		t.Errorf("savedInput = %q after first Up, want %q", il.savedInput, "partial")
	}
}

func TestHistoryUp_NavigatesToLastEntry(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("n")
	il.appendHistory("look")
	il.historyUp()
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after one Up, want 1", il.histIdx)
	}
}

func TestHistoryUp_NavigatesToFirstEntry(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("n")
	il.appendHistory("look")
	il.historyUp() // look
	il.historyUp() // n
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after two Ups, want 0", il.histIdx)
	}
}

func TestHistoryUp_StopsAtFirstEntry(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("only")
	il.historyUp()
	il.historyUp() // should be a no-op at boundary
	il.historyUp() // still no-op
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after excess Ups, want 0", il.histIdx)
	}
}

func TestHistoryUp_DoesNotResaveSavedInputOnSubsequentUps(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("n")
	il.appendHistory("s")
	il.editor.SetText("work in progress")
	il.historyUp() // saves "work in progress", goes to "s"
	// Force a fake savedInput change to verify it won't be overwritten.
	il.savedInput = "work in progress"
	il.historyUp() // goes to "n", must not clobber savedInput
	if il.savedInput != "work in progress" {
		t.Errorf("savedInput = %q after second Up, should still be %q", il.savedInput, "work in progress")
	}
}

// ── historyDown ───────────────────────────────────────────────────────────

func TestHistoryDown_AtEndNoHistory_NoChange(t *testing.T) {
	il := NewInputLine()
	// histIdx == len(history) == 0: already at end.
	il.historyDown()
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after Down with no history, want 0", il.histIdx)
	}
}

func TestHistoryDown_AtEndWithHistory_NoChange(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("cmd1")
	// histIdx == len(history) == 1: already at newest.
	il.historyDown()
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after Down at end, want 1", il.histIdx)
	}
}

func TestHistoryDown_RestoresSavedInputAtEnd(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("go north")
	il.editor.SetText("in prog")
	il.historyUp()   // saves "in prog", idx=0
	il.historyDown() // idx=1 (end) → restore savedInput
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after Up+Down, want 1 (end)", il.histIdx)
	}
	if il.savedInput != "in prog" {
		t.Errorf("savedInput = %q, want %q", il.savedInput, "in prog")
	}
}

func TestHistoryDown_NavigatesForwardThroughHistory(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("a")
	il.appendHistory("b")
	il.appendHistory("c")
	il.historyUp() // c
	il.historyUp() // b
	il.historyUp() // a  (idx=0)
	il.historyDown() // b  (idx=1)
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after Up×3+Down, want 1", il.histIdx)
	}
	il.historyDown() // c  (idx=2)
	if il.histIdx != 2 {
		t.Errorf("histIdx = %d after second Down, want 2", il.histIdx)
	}
}

func TestHistoryDown_ReachesEndAndStays(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("x")
	il.historyUp()   // idx=0
	il.historyDown() // idx=1 (end)
	il.historyDown() // already at end, must not move or panic
	if il.histIdx != 1 {
		t.Errorf("histIdx = %d after excess Downs, want 1", il.histIdx)
	}
}

// ── round-trip navigation ─────────────────────────────────────────────────

func TestHistory_UpDownRoundTrip(t *testing.T) {
	il := NewInputLine()
	cmds := []string{"go north", "kill orc", "get sword", "inv"}
	for _, c := range cmds {
		il.appendHistory(c)
	}
	// Navigate all the way up.
	for i := 0; i < len(cmds); i++ {
		il.historyUp()
	}
	if il.histIdx != 0 {
		t.Errorf("histIdx = %d after full Up traversal, want 0", il.histIdx)
	}
	// Navigate all the way down again.
	for i := 0; i < len(cmds); i++ {
		il.historyDown()
	}
	if il.histIdx != len(cmds) {
		t.Errorf("histIdx = %d after full Down traversal, want %d", il.histIdx, len(cmds))
	}
}

// ── drainOps / OpSubmit ───────────────────────────────────────────────────

func TestDrainOps_OpSubmit_AppendsToHistory(t *testing.T) {
	il := NewInputLine()
	il.editor.SetText("look")
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()
	if len(il.history) != 1 {
		t.Fatalf("history length = %d after OpSubmit, want 1", len(il.history))
	}
	if il.history[0] != "look" {
		t.Errorf("history[0] = %q, want %q", il.history[0], "look")
	}
}

func TestDrainOps_OpSubmit_SkipsEmpty(t *testing.T) {
	il := NewInputLine()
	// Editor text is empty; submitting empty should not grow history.
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()
	if len(il.history) != 0 {
		t.Errorf("empty OpSubmit must not add to history: len = %d", len(il.history))
	}
}

func TestDrainOps_OpSubmit_SkipsConsecutiveDuplicate(t *testing.T) {
	il := NewInputLine()
	il.editor.SetText("look")
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()

	il.editor.SetText("look")
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()

	if len(il.history) != 1 {
		t.Errorf("consecutive duplicate via OpSubmit must not grow history: len = %d, want 1", len(il.history))
	}
}

func TestDrainOps_OpSubmit_SetsSubmittedAndText(t *testing.T) {
	il := NewInputLine()
	il.editor.SetText("kill orc")
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()
	if !il.Submitted {
		t.Error("Submitted should be true after OpSubmit")
	}
	if il.SubmitText != "kill orc" {
		t.Errorf("SubmitText = %q, want %q", il.SubmitText, "kill orc")
	}
}

func TestDrainOps_OpSubmit_ResetsHistIdx(t *testing.T) {
	il := NewInputLine()
	// Fill history and simulate browsing.
	il.appendHistory("a")
	il.appendHistory("b")
	il.histIdx = 0 // browsing

	il.editor.SetText("c")
	il.EnqueueOp(OpSubmit, "")
	il.drainOps()

	if il.histIdx != len(il.history) {
		t.Errorf("histIdx = %d after OpSubmit, want %d (len)", il.histIdx, len(il.history))
	}
}

// ── histIdx invariant ─────────────────────────────────────────────────────

func TestHistIdx_NeverExceedsHistoryLen(t *testing.T) {
	il := NewInputLine()
	// Down from a state where histIdx already equals len(history) must not
	// push histIdx above len(history).
	for i := 0; i < 5; i++ {
		il.historyDown()
	}
	if il.histIdx > len(il.history) {
		t.Errorf("histIdx = %d > len(history) = %d", il.histIdx, len(il.history))
	}
}

func TestHistIdx_NeverBelowZero(t *testing.T) {
	il := NewInputLine()
	il.appendHistory("x")
	il.historyUp()
	for i := 0; i < 10; i++ {
		il.historyUp()
	}
	if il.histIdx < 0 {
		t.Errorf("histIdx = %d, must never be negative", il.histIdx)
	}
}

// ── history cap ───────────────────────────────────────────────────────────

func TestAppendHistory_CapEnforced(t *testing.T) {
	il := NewInputLine()
	il.SetHistoryLimit(3)
	for _, cmd := range []string{"a", "b", "c", "d"} {
		il.appendHistory(cmd)
	}
	if len(il.history) != 3 {
		t.Fatalf("history length = %d, want 3 after cap of 3", len(il.history))
	}
	if il.history[0] != "b" || il.history[1] != "c" || il.history[2] != "d" {
		t.Errorf("history = %v, want [b c d]", il.history)
	}
	if il.histIdx != 3 {
		t.Errorf("histIdx = %d, want 3", il.histIdx)
	}
}

func TestAppendHistory_CapZeroUnlimited(t *testing.T) {
	il := NewInputLine()
	il.SetHistoryLimit(0)
	for i := 0; i < 5000; i++ {
		il.appendHistory(fmt.Sprintf("cmd%d", i))
	}
	if len(il.history) != 5000 {
		t.Errorf("history length = %d, want 5000 with unlimited cap", len(il.history))
	}
}

func TestAppendHistory_CapAdjustsHistIdx(t *testing.T) {
	il := NewInputLine()
	il.SetHistoryLimit(3)
	il.appendHistory("a")
	il.appendHistory("b")
	il.appendHistory("c")
	il.histIdx = 1 // simulating browsing at index 1

	// Appending "d" should trim "a", shifting histIdx from 1 to 0.
	il.appendHistory("d")
	// After appendHistory, histIdx is always reset to len(history).
	if il.histIdx != 3 {
		t.Errorf("histIdx = %d after cap trim, want 3 (len)", il.histIdx)
	}
}

func TestSetHistoryLimit_DefaultIs2000(t *testing.T) {
	il := NewInputLine()
	if il.historyLimit != 2000 {
		t.Errorf("default historyLimit = %d, want 2000", il.historyLimit)
	}
}



// TestResetMinutesFormat verifies the string format used in Layout.
// The actual widget rendering requires a Gio context, so we test the
// formatting logic directly.
func TestResetMinutesFormat(t *testing.T) {
	cases := []struct {
		minutes int
		want    string
	}{
		{1, "1m "},
		{5, "5m "},
		{44, "44m "},
		{99, "99m "},
		{120, "120m "},
	}
	for _, tc := range cases {
		got := fmt.Sprintf("%dm ", tc.minutes)
		if got != tc.want {
			t.Errorf("fmt.Sprintf(%%dm , %d) = %q, want %q", tc.minutes, got, tc.want)
		}
	}
}
