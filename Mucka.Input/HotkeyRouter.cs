namespace Mucka.Input;

/// <summary>Modifier state for a key press, platform-neutral.</summary>
[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// One key press, reduced to the only two things a binding can depend on. <paramref name="KeyCode"/>
/// is the platform's own key value, passed through as an opaque int rather than re-enumerated here:
/// mapping every key of every platform into a private enum would be a large, dull, and permanently
/// incomplete translation layer, and nothing in this project needs to know what the number MEANS.
/// </summary>
public readonly record struct Hotkey(int KeyCode, InputModifiers Modifiers);

/// <summary>
/// Declarative hotkey dispatch: bindings are registered up front, and a key press costs one
/// dictionary probe.
///
/// <para><b>Why this exists.</b> Hotkey handling used to be a chain of <c>if</c>s inside the command
/// box's own key handler - which meant consumer logic ran ON the keystroke, and that every plain
/// letter typed paid for the tests that decided it was not a hotkey. One of those tests was a
/// <c>GetKeyState</c> P/Invoke, executed per character, purely to discover that Ctrl was not held.
/// Here a plain letter costs a struct hash and a miss.</para>
///
/// <para><b>Decide synchronously, act asynchronously.</b> <see cref="Handle"/> answers "is this key
/// mine?" immediately - the caller needs that answer to suppress the control's own handling of the
/// key, and it cannot wait. The ACTION, though, goes through <see cref="InputGate"/> and runs on the
/// drain. So a binding may do as much work as it likes and still cannot stall typing, which is what
/// makes this safe to extend: adding a hotkey is no longer a decision about the input path.</para>
///
/// <para>Routing through the same gate as typed lines also keeps the two in order. A hotkey pressed
/// after a command was typed takes its turn behind that command, which for keys like flee is the
/// difference between the game seeing what the player meant and seeing it backwards.</para>
/// </summary>
public sealed class HotkeyRouter
{
    private readonly InputGate _gate;
    private readonly Dictionary<Hotkey, Binding> _bindings = new();

    /// <summary>Key codes bound under ANY modifier combination, so a caller can rule a key out before
    /// it pays to discover the modifier state - see <see cref="IsBoundKey"/>.</summary>
    private readonly HashSet<int> _boundKeyCodes = new();

    private readonly record struct Binding(string Name, Action Action, bool Immediate);

    public HotkeyRouter(InputGate gate)
        => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>Bindings currently registered. Diagnostic only.</summary>
    public int Count => _bindings.Count;

    /// <summary>The name of the last binding <see cref="Handle"/> matched, for diagnostics - "which
    /// hotkey ate my keystroke" is otherwise an awkward question to answer.</summary>
    public string? LastMatched { get; private set; }

    /// <summary>
    /// Registers a hotkey whose action runs on the gate's drain, off the input path. This is the
    /// normal form and should stay the overwhelming majority.
    /// </summary>
    /// <param name="name">For diagnostics; also what makes a duplicate registration legible.</param>
    public void Bind(int keyCode, InputModifiers modifiers, string name, Action action)
        => Add(new Hotkey(keyCode, modifiers), new Binding(name, action, Immediate: false));

    /// <summary>
    /// Registers a hotkey whose action runs SYNCHRONOUSLY, on the keystroke.
    ///
    /// <para>Reserved for the case where the effect must be complete before the key event returns -
    /// in practice, only where the control's own default handling would otherwise observe a state we
    /// were about to change. Needing this is a smell worth a second look: the action becomes part of
    /// the typing path, with everything CLAUDE.md's Invariant #1 says about that, and it must
    /// therefore be trivial and stay trivial. Kept as a separate method rather than a boolean
    /// parameter so that every use of it is greppable.</para>
    /// </summary>
    public void BindImmediate(int keyCode, InputModifiers modifiers, string name, Action action)
        => Add(new Hotkey(keyCode, modifiers), new Binding(name, action, Immediate: true));

    private void Add(Hotkey key, Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding.Action);
        if (_bindings.TryGetValue(key, out var existing))
            throw new InvalidOperationException(
                $"Hotkey {key.Modifiers}+{key.KeyCode} is already bound to '{existing.Name}'; "
                + $"refusing to shadow it with '{binding.Name}'. Two features silently fighting over "
                + "one key is a bug the player experiences as the wrong thing happening.");
        _bindings[key] = binding;
        _boundKeyCodes.Add(key.KeyCode);
    }

    public void Clear()
    {
        _bindings.Clear();
        _boundKeyCodes.Clear();
    }

    /// <summary>
    /// Whether this key code participates in ANY binding, ignoring modifiers. False for every letter,
    /// digit and punctuation mark of ordinary typing.
    ///
    /// <para>Exists so that reading the modifier state can be skipped for keys that could not match
    /// anything. On Windows that state costs a <c>GetKeyState</c> P/Invoke per modifier, and the
    /// previous hand-rolled handler paid one on EVERY character typed just to find out that Ctrl was
    /// not held. Ask this first and plain typing costs a single hash lookup instead.</para>
    /// </summary>
    public bool IsBoundKey(int keyCode) => _boundKeyCodes.Contains(keyCode);

    /// <summary>
    /// Called for every key press, on the input path. Returns true when this router owns the key, in
    /// which case the caller must mark the event handled and let the control see nothing.
    ///
    /// <para>The whole cost for a key with no binding - which is every character of ordinary typing -
    /// is constructing a 8-byte struct and failing a dictionary lookup. Nothing else happens here,
    /// deliberately: this method is the narrowest part of the typing path and the easiest place in the
    /// codebase to accidentally make everything slow.</para>
    /// </summary>
    public bool Handle(int keyCode, InputModifiers modifiers)
    {
        if (!_bindings.TryGetValue(new Hotkey(keyCode, modifiers), out var binding))
            return false;

        LastMatched = binding.Name;
        if (binding.Immediate)
            binding.Action();       // see BindImmediate - synchronous by explicit request
        else
            _gate.Post(binding.Action);
        return true;
    }
}
