using MudSharp.Protocol;

namespace Mucka.Commands;

/// <summary>The navigation options a key can be given: eight compass points, the four other exits, the
/// swamp, and quick scan.</summary>
public enum NavAction
{
    NorthWest, North, NorthEast,
    West, Scan, East,
    SouthWest, South, SouthEast,
    Out, In, Up, Down, Swampward,
}

/// <summary>One option as the Navkeys tab lists it: what it is called, the line it sends, and the key
/// that does it until the player picks another. <see cref="Command"/> is empty for scan, which is not
/// a line but a command interrupt (<see cref="NavKeyMap.ScanBytes"/>).</summary>
public sealed record NavActionInfo(NavAction Action, string Label, string Command, string DefaultKey);

/// <summary>A key a navigation option can be given. <see cref="Name"/> is what mucka.ini stores and a
/// platform maps onto its own key code; <see cref="Key"/> is what <see cref="KeyCollision"/> looks at.</summary>
public sealed record NavKeyChoice(string Name, string Display, KeyRef Key);

/// <summary>
/// The navigation options and the keys they can be given. No MAUI and no key codes, so it is tested
/// here; a platform page owns the mapping from <see cref="NavKeyChoice.Name"/> to its own key values.
/// </summary>
public static class NavKeyMap
{
    /// <summary>The Swampward option's line.</summary>
    public const string SwampwardCommand = "zw";

    /// <summary>Quick scan as a command interrupt (ESC-[ ... ESC-]): the server runs it without echoing
    /// it and without touching the player's own line or its repeat-last-command history.</summary>
    public static readonly byte[] ScanBytes =
        System.Text.Encoding.Latin1.GetBytes("\x1b-[quickscan\x1b-]");

    /// <summary>Every option, in the number pad's own layout - rows top to bottom, then the bottom row
    /// and the keys down its right-hand side - which is the order of the Navkeys tab.</summary>
    public static readonly IReadOnlyList<NavActionInfo> Actions =
    [
        new(NavAction.NorthWest, "North-west", ExitWords.NorthWest, "Pad7"),
        new(NavAction.North,     "North",      ExitWords.North,     "Pad8"),
        new(NavAction.NorthEast, "North-east", ExitWords.NorthEast, "Pad9"),
        new(NavAction.West,      "West",       ExitWords.West,      "Pad4"),
        new(NavAction.Scan,      "Scan",       string.Empty,        "Pad5"),
        new(NavAction.East,      "East",       ExitWords.East,      "Pad6"),
        new(NavAction.SouthWest, "South-west", ExitWords.SouthWest, "Pad1"),
        new(NavAction.South,     "South",      ExitWords.South,     "Pad2"),
        new(NavAction.SouthEast, "South-east", ExitWords.SouthEast, "Pad3"),
        new(NavAction.Out,       "Out",        ExitWords.Out,       "Pad0"),
        new(NavAction.In,        "In",         ExitWords.In,        "PadMultiply"),
        new(NavAction.Up,        "Up",         ExitWords.Up,        "PadSubtract"),
        new(NavAction.Down,      "Down",       ExitWords.Down,      "PadAdd"),
        new(NavAction.Swampward, "Swampward",  SwampwardCommand,    "PadPeriod"),
    ];

    /// <summary>The keys the Navkeys tab offers: the number pad and F1-F12. The main block, Enter,
    /// Backspace and the modifiers are not offered, and <see cref="KeyCollision"/> refuses them anyway.</summary>
    public static readonly IReadOnlyList<NavKeyChoice> Choices = BuildChoices();

    private static List<NavKeyChoice> BuildChoices()
    {
        var choices = new List<NavKeyChoice>();
        for (var d = 0; d <= 9; d++)
            choices.Add(new($"Pad{d}", $"Numpad {d}", new KeyRef(KeyKind.Other)));
        choices.Add(new("PadMultiply", "Numpad *", new KeyRef(KeyKind.Other)));
        choices.Add(new("PadSubtract", "Numpad -", new KeyRef(KeyKind.Other)));
        choices.Add(new("PadAdd",      "Numpad +", new KeyRef(KeyKind.Other)));
        choices.Add(new("PadPeriod",   "Numpad .", new KeyRef(KeyKind.Other)));
        choices.Add(new("PadDivide",   "Numpad /", new KeyRef(KeyKind.Other)));
        for (var f = 1; f <= 12; f++)
            choices.Add(new($"F{f}", $"F{f}", new KeyRef(KeyKind.Function, f)));
        return choices;
    }

    public static NavActionInfo For(NavAction action) => Actions.First(a => a.Action == action);

    /// <summary>The key with this stored name, however it is cased; null for an empty or unknown name.</summary>
    public static NavKeyChoice? FindChoice(string? name)
        => string.IsNullOrWhiteSpace(name) ? null
            : Choices.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a key is, as far as <see cref="KeyCollision"/> is concerned. A platform classifies its
/// own key codes into these.</summary>
public enum KeyKind
{
    /// <summary>A key of the main block that types a character: letters, top-row digits, punctuation, space.</summary>
    Printable,
    Enter,
    Shift,
    Control,
    Backspace,
    /// <summary>F1 and up; <see cref="KeyRef.FunctionNumber"/> says which.</summary>
    Function,
    /// <summary>Anything else - the number pad's keys among them.</summary>
    Other,
}

/// <summary>A key to be claimed, reduced to what the collision rules look at.</summary>
public readonly record struct KeyRef(KeyKind Kind, int FunctionNumber = 0);

/// <summary>
/// Whether a key may be given a job of its own. The one rule for every feature that claims keys: the
/// navigation options now, and the F-key macros once their keys can be chosen.
///
/// <para>Refused: the main block's characters, Enter, Shift, Control, Backspace, and an F-key that has
/// a macro. An F-key with no macro is free.</para>
/// </summary>
public static class KeyCollision
{
    /// <summary>The reason the key cannot be claimed, or null when it can.</summary>
    /// <param name="fkeyHasValue">Whether the F-key with this 1-based number has a macro.</param>
    public static string? Check(KeyRef key, Func<int, bool> fkeyHasValue)
    {
        ArgumentNullException.ThrowIfNull(fkeyHasValue);
        return key.Kind switch
        {
            KeyKind.Printable => "it types a character",
            KeyKind.Enter => "it sends the typed line",
            KeyKind.Shift => "Shift is a modifier",
            KeyKind.Control => "Ctrl is a modifier",
            KeyKind.Backspace => "it edits the typed line",
            KeyKind.Function when fkeyHasValue(key.FunctionNumber) => $"F{key.FunctionNumber} has a macro",
            _ => null,
        };
    }
}

/// <summary>
/// The key each navigation option answers to; empty for an option with no key. Immutable: an edit makes
/// a new set.
///
/// <para>Stored in <c>[navkeys]</c> of mucka.ini, one key per <see cref="NavAction"/> named by
/// <see cref="IniKey"/>, holding a <see cref="NavKeyChoice.Name"/>. An option the file lacks, or holds an
/// unknown key name for, takes its default; one present and empty has no key.</para>
/// </summary>
public sealed class NavKeyBindings
{
    public const string Section = "navkeys";

    public static NavKeyBindings Default { get; } = new(NavKeyMap.Actions.ToDictionary(a => a.Action, a => a.DefaultKey));

    private readonly Dictionary<NavAction, string> _keys;

    private NavKeyBindings(Dictionary<NavAction, string> keys) => _keys = keys;

    /// <summary>The option's name in <c>[navkeys]</c>: its <see cref="NavAction"/> name, lower-cased.</summary>
    public static string IniKey(NavAction action) => action.ToString().ToLowerInvariant();

    /// <summary>The <see cref="NavKeyChoice.Name"/> of the option's key; empty when it has none.</summary>
    public string Get(NavAction action) => _keys[action];

    /// <summary>The option with this key; an empty or unknown name leaves it with none.</summary>
    public NavKeyBindings With(NavAction action, string? keyName)
    {
        var copy = new Dictionary<NavAction, string>(_keys) { [action] = NavKeyMap.FindChoice(keyName)?.Name ?? string.Empty };
        return new NavKeyBindings(copy);
    }

    public bool IsSameAs(NavKeyBindings other)
        => NavKeyMap.Actions.All(a => Get(a.Action) == other.Get(a.Action));

    /// <summary>What is wrong with these bindings, one line each: a key <see cref="KeyCollision"/> refuses,
    /// or one given to two options. Empty when they can all be claimed.</summary>
    public IReadOnlyList<string> Problems(Func<int, bool> fkeyHasValue)
    {
        var problems = new List<string>();
        var firstUse = new Dictionary<string, NavActionInfo>();
        foreach (var info in NavKeyMap.Actions)
        {
            if (NavKeyMap.FindChoice(Get(info.Action)) is not { } choice)
                continue;
            if (KeyCollision.Check(choice.Key, fkeyHasValue) is { } reason)
                problems.Add($"{info.Label} cannot use {choice.Display}: {reason}.");
            else if (firstUse.TryGetValue(choice.Name, out var other))
                problems.Add($"{other.Label} and {info.Label} both use {choice.Display}.");
            else
                firstUse[choice.Name] = info;
        }
        return problems;
    }

    /// <summary>The keys that can be claimed right now, each with its option: those <see cref="Problems"/>
    /// does not report, the first option winning a key two of them share.</summary>
    public IReadOnlyList<(NavKeyChoice Key, NavAction Action)> Claims(Func<int, bool> fkeyHasValue)
    {
        var claims = new List<(NavKeyChoice, NavAction)>();
        foreach (var info in NavKeyMap.Actions)
        {
            if (NavKeyMap.FindChoice(Get(info.Action)) is not { } choice
                || KeyCollision.Check(choice.Key, fkeyHasValue) is not null
                || claims.Any(c => c.Item1.Name == choice.Name))
                continue;
            claims.Add((choice, info.Action));
        }
        return claims;
    }

    /// <summary>The bindings stored under <see cref="Section"/>, from a lookup returning null for an absent key.</summary>
    public static NavKeyBindings Read(Func<string, string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var keys = new Dictionary<NavAction, string>();
        foreach (var info in NavKeyMap.Actions)
        {
            var stored = get(Section, IniKey(info.Action));
            keys[info.Action] = stored is null ? info.DefaultKey
                : stored.Trim().Length == 0 ? string.Empty
                : NavKeyMap.FindChoice(stored)?.Name ?? info.DefaultKey;
        }
        return new NavKeyBindings(keys);
    }

    /// <summary>Writes every option, so one with no key is stored as empty rather than reading back as its default.</summary>
    public void Write(Action<string, string, string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var info in NavKeyMap.Actions)
            set(Section, IniKey(info.Action), Get(info.Action));
    }
}

/// <summary>
/// Decides whether a quick scan goes out: at most one a second, unless a move was made since the last
/// one - after a move the room is new and the scan is wanted at once. The clock is injected so the
/// spacing is tested without waiting.
/// </summary>
public sealed class NavScanGate
{
    public static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);

    private readonly Func<DateTime> _utcNow;
    private DateTime? _lastScanUtc;
    private bool _movedSinceScan;

    public NavScanGate(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>A move was made, by any means that calls this.</summary>
    public void Moved() => _movedSinceScan = true;

    /// <summary>True when a scan should be sent now; the call counts as that scan.</summary>
    public bool TryScan()
    {
        var now = _utcNow();
        if (_lastScanUtc is { } last && !_movedSinceScan && now - last < MinSpacing)
            return false;
        _lastScanUtc = now;
        _movedSinceScan = false;
        return true;
    }
}
