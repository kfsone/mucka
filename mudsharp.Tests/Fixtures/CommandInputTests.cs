using Mucka.Input;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The command-input framework's rules, as tests.
///
/// <para>These exist because the command box is the one part of this application whose failures the
/// owner experiences as unacceptable rather than annoying, and because it has now been broken three
/// times by three different well-meant additions. Comments did not stop that. The point of
/// Mucka.Input is that its rules are now (a) behind an assembly boundary, so app code cannot reach
/// past them, and (b) platform-free, so they can be proven here instead of discovered at 120 wpm.</para>
///
/// <para>Every test below is a rule someone could plausibly break by accident. If one starts failing,
/// the fix is almost certainly not the test.</para>
/// </summary>
public class CommandInputTests
{
    /// <summary>A surface that records what was done to it, standing in for the native control. It is
    /// deliberately dumb: the real adapter must be this dumb too, because it lives on the far side of
    /// the wall where these tests cannot see it.</summary>
    private sealed class FakeSurface : IInputSurface
    {
        public string Text { get; set; } = string.Empty;
        public List<string> Log { get; } = new();

        public int Caret { get; private set; }
        public void SetText(string text, int caret) { Text = text; Caret = caret; Log.Add($"SetText:{text}@{caret}"); }
        public void Clear() { Text = string.Empty; Log.Add("Clear"); }
        public void Focus() => Log.Add("Focus");
    }

    /// <summary>Holds posted work until a test chooses to run it, which is what makes "did this happen
    /// ON the keystroke or after it" an observable distinction rather than a timing guess.</summary>
    private sealed class ManualPump
    {
        private readonly Queue<Action> _posted = new();
        public int PostCount { get; private set; }
        public void Post(Action work) { PostCount++; _posted.Enqueue(work); }
        public int Pending => _posted.Count;
        public void RunAll()
        {
            while (_posted.Count > 0)
                _posted.Dequeue()();
        }
    }

    private const int KeyEnter = 13;
    private const int KeyUp = 38;
    private const int KeyEscape = 27;
    private const int KeyN = 78;

    private static (CommandInput Input, FakeSurface Surface, ManualPump Pump, List<string> Lines,
        List<string> Reports) Build()
    {
        var surface = new FakeSurface();
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var reports = new List<string>();
        var input = new CommandInput(surface, gate, new InputPathBudget(reports.Add));
        var lines = new List<string>();
        gate.LineReady += lines.Add;
        return (input, surface, pump, lines, reports);
    }

    // ---- The accept path -------------------------------------------------------------------------

    /// <summary>
    /// The rule that the whole framework is for: pressing Enter empties the box IMMEDIATELY, and the
    /// line is delivered later. Nothing downstream can make the box wait.
    /// </summary>
    [Fact]
    public void Accept_EmptiesTheBoxOnTheKeystroke_AndDeliversTheLineAfterwards()
    {
        var (input, surface, pump, lines, _) = Build();
        surface.Text = "north";

        Assert.True(input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true));

        // Synchronously: the box is already empty.
        Assert.Equal(string.Empty, surface.Text);
        // And nothing has been interpreted yet - that is the whole separation.
        Assert.Empty(lines);

        pump.RunAll();
        Assert.Equal(["north"], lines);
    }

    /// <summary>
    /// The exact reported corruption, as a regression test. A character that lands in the box AFTER an
    /// Enter must start the next line, never join the one already accepted - and the accepted line
    /// must not have been mutated by it.
    /// </summary>
    [Fact]
    public void Accept_TextArrivingAfterEnter_StartsTheNextLine()
    {
        var (input, surface, pump, lines, _) = Build();

        surface.Text = "n";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        // A late keystroke lands in the (now empty) box, as if the platform applied it after the key
        // event - the ordering hazard that put "nne" on the wire.
        surface.Text = "n";
        surface.Text = "ne";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);

        pump.RunAll();
        Assert.Equal(["n", "ne"], lines);
    }

    /// <summary>A bare Enter is a real MUD2 action and must go out as an empty line, not be
    /// "helpfully" swallowed - while still being counted, because an unexpected empty line is also the
    /// signature of a failed read.</summary>
    [Fact]
    public void Accept_EmptyLine_IsSentAndCounted()
    {
        var (input, surface, pump, lines, _) = Build();
        surface.Text = string.Empty;

        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        pump.RunAll();

        Assert.Equal([string.Empty], lines);
        Assert.Equal(1, input.EmptyLineCount);
    }

    /// <summary>Several Enters before the drain runs - the burst case - must arrive in the order they
    /// were typed and none may be lost.</summary>
    [Fact]
    public void Accept_ABurstOfLines_KeepsOrderAndLosesNothing()
    {
        var (input, surface, pump, lines, _) = Build();

        foreach (var cmd in new[] { "n", "ne", "e", "se" })
        {
            surface.Text = cmd;
            input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        }
        Assert.Empty(lines);        // still nothing interpreted
        pump.RunAll();

        Assert.Equal(["n", "ne", "e", "se"], lines);
        Assert.Equal(4, input.Gate.AcceptedCount);
    }

    // ---- Hotkeys ---------------------------------------------------------------------------------

    /// <summary>An unbound key - i.e. every character of ordinary typing - is not owned, so the
    /// control handles it and nothing of ours runs.</summary>
    [Fact]
    public void PlainTyping_IsNotOwned_AndCostsNoPostedWork()
    {
        var (input, _, pump, _, _) = Build();
        input.Hotkeys.Bind(KeyUp, InputModifiers.None, "history-up", () => { });

        Assert.False(input.HandleKey(KeyN, InputModifiers.None, isAcceptKey: false));
        Assert.Equal(0, pump.PostCount);
    }

    /// <summary>A bound hotkey is owned synchronously (the caller needs that answer at once to
    /// suppress the control) but its action runs later, off the keystroke.</summary>
    [Fact]
    public void Hotkey_IsOwnedSynchronously_ButActsOnTheDrain()
    {
        var (input, _, pump, _, _) = Build();
        var ran = false;
        input.Hotkeys.Bind(KeyUp, InputModifiers.None, "history-up", () => ran = true);

        Assert.True(input.HandleKey(KeyUp, InputModifiers.None, isAcceptKey: false));
        Assert.False(ran);          // NOT on the keystroke

        pump.RunAll();
        Assert.True(ran);
    }

    /// <summary>Modifiers are part of the identity of a binding: Ctrl+1 and a bare 1 are different
    /// keys, and typing "1" into a command must not fire a macro.</summary>
    [Fact]
    public void Hotkey_ModifiersDistinguishBindings()
    {
        var (input, _, pump, _, _) = Build();
        var hits = new List<string>();
        input.Hotkeys.Bind('1', InputModifiers.Control, "macro-1", () => hits.Add("ctrl"));

        Assert.False(input.HandleKey('1', InputModifiers.None, isAcceptKey: false));
        Assert.True(input.HandleKey('1', InputModifiers.Control, isAcceptKey: false));
        pump.RunAll();

        Assert.Equal(["ctrl"], hits);
    }

    /// <summary>Two features silently fighting over one key is a bug the player experiences as the
    /// wrong thing happening, so the second registration is refused loudly at startup instead.</summary>
    [Fact]
    public void Hotkey_DuplicateBinding_IsRejected()
    {
        var (input, _, _, _, _) = Build();
        input.Hotkeys.Bind(KeyEscape, InputModifiers.None, "clear-input", () => { });

        var ex = Assert.Throws<InvalidOperationException>(
            () => input.Hotkeys.Bind(KeyEscape, InputModifiers.None, "close-overlay", () => { }));
        Assert.Contains("clear-input", ex.Message);
    }

    /// <summary>The escape hatch works, and is visibly an escape hatch.</summary>
    [Fact]
    public void ImmediateHotkey_RunsOnTheKeystroke()
    {
        var (input, _, _, _, _) = Build();
        var ran = false;
        input.Hotkeys.BindImmediate(KeyEscape, InputModifiers.None, "close-overlay", () => ran = true);

        input.HandleKey(KeyEscape, InputModifiers.None, isAcceptKey: false);
        Assert.True(ran);
    }

    /// <summary>
    /// The cheap pre-filter that keeps plain typing off the modifier-reading path entirely. Ordinary
    /// characters are not bound under any modifier, so a caller can stop before paying for the
    /// platform's key-state calls - which is what the previous hand-rolled handler did on every single
    /// character typed.
    /// </summary>
    [Fact]
    public void IsBoundKey_RulesOutOrdinaryCharactersWithoutConsideringModifiers()
    {
        var (input, _, _, _, _) = Build();
        input.Hotkeys.Bind('1', InputModifiers.Control, "macro-1", () => { });
        input.Hotkeys.Bind(KeyUp, InputModifiers.None, "history-up", () => { });

        Assert.False(input.Hotkeys.IsBoundKey(KeyN));      // a letter: never a hotkey
        Assert.True(input.Hotkeys.IsBoundKey('1'));        // bound, though only WITH Ctrl
        Assert.True(input.Hotkeys.IsBoundKey(KeyUp));
    }

    // ---- Ordering across everything the player does ----------------------------------------------

    /// <summary>
    /// The ordering guarantee that a split design cannot give: a hotkey pressed after a line was typed
    /// acts AFTER that line. When the hotkey is flee, in a permadeath game, this is the difference
    /// between the game seeing what the player meant and seeing it backwards.
    /// </summary>
    [Fact]
    public void TypedLinesAndHotkeys_ReachTheGameInThePlayersOrder()
    {
        var (input, surface, pump, _, _) = Build();
        var wire = new List<string>();
        input.Gate.LineReady += l => wire.Add($"line:{l}");
        input.Hotkeys.Bind('F', InputModifiers.Control, "flee", () => wire.Add("flee"));

        surface.Text = "kill rat";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);   // typed first
        input.HandleKey('F', InputModifiers.Control, isAcceptKey: false);    // panicked second
        pump.RunAll();

        Assert.Equal(["line:kill rat", "flee"], wire);
    }

    /// <summary>Requests to write into the box queue behind what the player has already typed, so a
    /// feature cannot land text in the middle of a line in progress.</summary>
    [Fact]
    public void RequestSetText_QueuesBehindTypedInput()
    {
        var (input, surface, pump, lines, _) = Build();

        surface.Text = "north";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        input.RequestSetText("tell fred ");

        // Not applied yet: the accepted line is still ahead of it in the queue.
        Assert.Equal(string.Empty, surface.Text);
        pump.RunAll();

        Assert.Equal(["north"], lines);
        Assert.Equal("tell fred ", surface.Text);
        Assert.Equal("tell fred ".Length, surface.Caret);
    }

    /// <summary>A prefix the player is expected to type after leaves the caret where it was asked for,
    /// not at the end - the case that used to justify reaching past the boundary to the control.</summary>
    [Fact]
    public void RequestSetText_WithACaret_LeavesItWhereAsked()
    {
        var (input, surface, pump, _, _) = Build();

        input.RequestSetText("Ollie \"", 6);
        pump.RunAll();

        Assert.Equal("Ollie \"", surface.Text);
        Assert.Equal(6, surface.Caret);
    }

    /// <summary>A caret outside the text is clamped rather than thrown at the platform, where an
    /// out-of-range SelectionStart is an exception in the middle of the typing path.</summary>
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 5)]
    public void RequestSetText_ClampsAnImpossibleCaret(int asked, int expected)
    {
        var (input, surface, pump, _, _) = Build();

        input.RequestSetText("north", asked);
        pump.RunAll();

        Assert.Equal(expected, surface.Caret);
    }

    /// <summary>
    /// A set-text request must reach the surface even when the text is IDENTICAL to what is already
    /// there. Unconditional delivery is the whole contract, and its absence has now caused two live
    /// bugs of the same shape.
    ///
    /// <para>Both came from inferring a UI update from a value CHANGING rather than asserting it:
    /// BaseViewModel.Set raises no PropertyChanged for an unchanged value, so a clear-on-send whose
    /// value was already empty cleared nothing (leaving typed text to corrupt the next command), and a
    /// history recall of an entry equal to the view model's copy showed nothing while the history index
    /// moved anyway ("cursor up doesn't always recover the last line typed"). This framework must never
    /// acquire that behaviour: it does not compare, it delivers.</para>
    /// </summary>
    [Fact]
    public void RequestSetText_DeliversEvenWhenTheTextIsUnchanged()
    {
        var (input, surface, pump, _, _) = Build();

        input.RequestSetText("north");
        pump.RunAll();
        surface.Log.Clear();

        // Same string again. A comparison anywhere in the path would swallow this.
        input.RequestSetText("north");
        pump.RunAll();

        Assert.Equal(["SetText:north@5"], surface.Log);
    }

    /// <summary>Same guarantee for the clear: emptying an already-empty box must still be a real call,
    /// because "empty" is a state to assert after an accept, not a condition to test for.</summary>
    [Fact]
    public void RequestClear_DeliversEvenWhenTheBoxIsAlreadyEmpty()
    {
        var (input, surface, pump, _, _) = Build();
        Assert.Equal(string.Empty, surface.Text);
        surface.Log.Clear();

        input.RequestClear();
        pump.RunAll();

        Assert.Equal(["Clear"], surface.Log);
    }

    /// <summary>
    /// Walking history must show a DIFFERENT entry on each press even when consecutive entries are
    /// identical - the case that produced the report. Three sends of "n" then three presses of Up must
    /// deliver three set-text calls, not one.
    /// </summary>
    [Fact]
    public void RepeatedIdenticalRecalls_EachReachTheBox()
    {
        var (input, surface, pump, _, _) = Build();
        var recalled = new[] { "n", "n", "n" };
        surface.Log.Clear();

        foreach (var entry in recalled)
            input.RequestSetText(entry);
        pump.RunAll();

        Assert.Equal(3, surface.Log.Count);
    }

    /// <summary>One misbehaving consumer must not take the queue down with it - the player typed three
    /// commands and is owed all three, whatever bug the middle one hits.</summary>
    [Fact]
    public void AThrowingConsumer_DoesNotStrandTheLinesBehindIt()
    {
        var surface = new FakeSurface();
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var seen = new List<string>();
        var faults = new List<Exception>();
        gate.Faulted += faults.Add;
        gate.LineReady += l =>
        {
            seen.Add(l);
            if (l == "boom") throw new InvalidOperationException("consumer bug");
        };
        var input = new CommandInput(surface, gate, new InputPathBudget(_ => { }));

        foreach (var cmd in new[] { "first", "boom", "third" })
        {
            surface.Text = cmd;
            input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        }
        pump.RunAll();

        Assert.Equal(["first", "boom", "third"], seen);
        Assert.Single(faults);
    }

    /// <summary>Work queued by a queued item joins the same pass, in creation order - a client command
    /// that sends a line must not have that send deferred to a later turn.</summary>
    [Fact]
    public void WorkQueuedDuringTheDrain_RunsInTheSamePass()
    {
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var order = new List<string>();

        gate.Post(() =>
        {
            order.Add("outer");
            gate.Post(() => order.Add("inner"));
        });
        gate.Post(() => order.Add("second"));
        pump.RunAll();

        Assert.Equal(["outer", "second", "inner"], order);
        Assert.Equal(1, pump.PostCount);   // one post for the whole burst, not one per item
    }

    /// <summary>Flush exists for code about to write to the socket directly; it must run pending work
    /// on the spot so nothing it sends can overtake a queued line.</summary>
    [Fact]
    public void Flush_RunsPendingWorkImmediately()
    {
        var (input, surface, _, lines, _) = Build();
        surface.Text = "north";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);

        Assert.Empty(lines);
        input.Gate.Flush();
        Assert.Equal(["north"], lines);
    }

    /// <summary>A drain must not re-enter itself via Flush.</summary>
    [Fact]
    public void Flush_FromInsideTheDrain_IsIgnored()
    {
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var runs = 0;
        gate.Post(() => { runs++; gate.Flush(); });
        pump.RunAll();
        Assert.Equal(1, runs);
    }

    // ---- The budget ------------------------------------------------------------------------------

    /// <summary>Ordinary key handling is far inside budget, so the guard is silent - it has to be, or
    /// it would be noise and get ignored.</summary>
    [Fact]
    public void Budget_StaysSilentForNormalKeyHandling()
    {
        var (input, surface, pump, _, reports) = Build();
        for (var i = 0; i < 200; i++)
            input.HandleKey(KeyN, InputModifiers.None, isAcceptKey: false);
        surface.Text = "north";
        input.HandleKey(KeyEnter, InputModifiers.None, isAcceptKey: true);
        pump.RunAll();

        Assert.Empty(reports);
    }

    /// <summary>And it does fire when something on the path is slow - the mechanism that makes a
    /// future regression announce itself instead of waiting to be noticed mid-fight.</summary>
    [Fact]
    public void Budget_ReportsWorkThatOverrunsTheKeystroke()
    {
        var reports = new List<string>();
        var budget = new InputPathBudget(reports.Add, budgetMs: 0.5);

        budget.Measure("deliberately slow", () => Thread.Sleep(20));

        Assert.Single(reports);
        Assert.Contains("deliberately slow", reports[0]);
        Assert.Equal(1, budget.OverrunCount);
        Assert.True(budget.WorstMilliseconds >= 10.0);
    }

    /// <summary>An immediate hotkey's cost lands INSIDE the keystroke's budget, which is the honest
    /// accounting - that is what BindImmediate is trading away.</summary>
    [Fact]
    public void Budget_CountsImmediateHotkeyWorkAgainstTheKeystroke()
    {
        var surface = new FakeSurface();
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var reports = new List<string>();
        var input = new CommandInput(surface, gate, new InputPathBudget(reports.Add, budgetMs: 0.5));
        input.Hotkeys.BindImmediate(KeyEscape, InputModifiers.None, "slow-thing", () => Thread.Sleep(20));

        input.HandleKey(KeyEscape, InputModifiers.None, isAcceptKey: false);

        Assert.Single(reports);
    }

    /// <summary>Whereas a normal binding's cost does NOT, because it never ran on the keystroke. This
    /// pair of tests is the framework's central claim, stated as arithmetic.</summary>
    [Fact]
    public void Budget_DoesNotCountPostedHotkeyWorkAgainstTheKeystroke()
    {
        var surface = new FakeSurface();
        var pump = new ManualPump();
        var gate = new InputGate(pump.Post);
        var reports = new List<string>();
        var input = new CommandInput(surface, gate, new InputPathBudget(reports.Add, budgetMs: 0.5));
        input.Hotkeys.Bind(KeyEscape, InputModifiers.None, "slow-thing", () => Thread.Sleep(20));

        input.HandleKey(KeyEscape, InputModifiers.None, isAcceptKey: false);
        pump.RunAll();

        Assert.Empty(reports);
    }
}
