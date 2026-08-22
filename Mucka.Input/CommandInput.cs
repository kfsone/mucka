namespace Mucka.Input;

/// <summary>
/// The command box's whole behaviour, on this side of the wall. Owns the accept path, the hotkey
/// table and the handoff; the app supplies only an <see cref="IInputSurface"/> adapter and a set of
/// declared bindings.
///
/// <para><b>The contract, in one sentence:</b> a key press goes in, and either a completed line comes
/// out on the gate's drain or a declared hotkey does - and nothing a consumer writes can run on the
/// keystroke itself.</para>
///
/// <para><b>What a consumer may do to the box.</b> Ask for text to be put in it
/// (<see cref="RequestSetText"/>), ask for it to be emptied (<see cref="RequestClear"/>), ask for
/// focus (<see cref="RequestFocus"/>). That is all, and all three are REQUESTS routed through the
/// gate rather than writes applied on the spot. This is the part that makes interference structurally
/// awkward: there is no handle to the control to be found, and the requests queue behind whatever the
/// player has already typed, so a feature cannot land text in the box between a keystroke and its
/// Enter.</para>
///
/// <para><b>What it deliberately does NOT do:</b> autocomplete, expansion, history, command parsing,
/// or anything else that inspects a line. The owner's standing position is that the box has one job -
/// capture and enqueue user input correctly and smoothly - and every one of those features belongs on
/// the far side of <see cref="InputGate"/>. If a future feature seems to need to look at the text as
/// it is typed, that is a design conversation, not a small addition here.</para>
/// </summary>
public sealed class CommandInput
{
    private readonly IInputSurface _surface;
    private readonly InputGate _gate;
    private readonly HotkeyRouter _hotkeys;
    private readonly InputPathBudget _budget;

    public CommandInput(IInputSurface surface, InputGate gate, InputPathBudget budget)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _hotkeys = new HotkeyRouter(gate);
    }

    /// <summary>Declare hotkeys here. Registration is app-side configuration; dispatch is this
    /// project's business.</summary>
    public HotkeyRouter Hotkeys => _hotkeys;

    /// <summary>The handoff every accepted line and every hotkey action travels through. Consumers
    /// subscribe to <see cref="InputGate.LineReady"/>.</summary>
    public InputGate Gate => _gate;

    /// <summary>Lines accepted but whose text was empty - a bare Enter, which is a real MUD2 action
    /// and must never be swallowed. Counted separately because "the box sent an empty line" is also
    /// the signature of a failed read, and telling the two apart matters.</summary>
    public long EmptyLineCount { get; private set; }

    /// <summary>
    /// Feeds one key press in. Returns true when the framework owned the key and the caller must
    /// suppress the control's own handling of it.
    ///
    /// <para>Ordering inside this method is the load-bearing part, and it is: read, empty, hand off.
    /// The box is emptied BEFORE the line goes anywhere, so it is never waiting on downstream work
    /// and there is no window in which a stale character can be left behind to join the next command.
    /// That last failure is not hypothetical - it put <c>nne</c> on the wire when the owner typed
    /// <c>n</c>, Enter, <c>ne</c>, Enter.</para>
    /// </summary>
    /// <param name="keyCode">The platform's key value - see <see cref="Hotkey"/>.</param>
    /// <param name="isAcceptKey">True for the key that completes a line (Enter). Passed in rather
    /// than compared against a constant here because the value is the platform's, and this project
    /// deliberately knows nothing about what any particular key code means.</param>
    public bool HandleKey(int keyCode, InputModifiers modifiers, bool isAcceptKey)
    {
        var handled = false;
        // The budget wraps everything we do on the keystroke, which is the number that matters to the
        // player - not any single contributor to it.
        _budget.Measure(isAcceptKey ? "accept" : "key", () =>
        {
            if (isAcceptKey)
            {
                AcceptLine();
                handled = true;
                return;
            }
            handled = _hotkeys.Handle(keyCode, modifiers);
        });
        return handled;
    }

    /// <summary>
    /// Takes what is in the box, empties it, and hands the line off. Three steps and no fourth.
    /// </summary>
    private void AcceptLine()
    {
        var line = _surface.Text;
        // Emptied first, unconditionally, and without consulting anything: the box's state after an
        // accept is not a conclusion to be derived from a notification chain (which is how a hole
        // opened here before - a chain that raised nothing when the value happened to be unchanged,
        // leaving typed text behind to corrupt the next command).
        _surface.Clear();
        if (line.Length == 0)
            EmptyLineCount++;
        _gate.AcceptLine(line);
    }

    /// <summary>Asks for text to be placed in the box, caret at the end - history recall, a clicked
    /// name, a reply prefix. Queued behind anything already typed, so it cannot interleave with a line
    /// in progress.</summary>
    public void RequestSetText(string text)
    {
        var value = text ?? string.Empty;
        _gate.Post(() => _surface.SetText(value, value.Length));
    }

    /// <summary>As <see cref="RequestSetText(string)"/>, but leaving the caret at
    /// <paramref name="caretPosition"/> - for a prefix the player is expected to type after (a clicked
    /// name, a reply lead-in).</summary>
    public void RequestSetText(string text, int caretPosition)
    {
        var value = text ?? string.Empty;
        var caret = Math.Clamp(caretPosition, 0, value.Length);
        _gate.Post(() => _surface.SetText(value, caret));
    }

    /// <summary>Asks for the box to be emptied (Escape).</summary>
    public void RequestClear() => _gate.Post(_surface.Clear);

    /// <summary>Asks for keyboard focus to come back - Invariant #0's mechanism. Queued like the
    /// rest: focus arriving a dispatcher turn later is imperceptible, and being in the queue means it
    /// cannot land in the middle of an accept.</summary>
    public void RequestFocus() => _gate.Post(_surface.Focus);
}
