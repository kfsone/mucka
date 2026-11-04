using Mucka.Commands;

namespace Mucka.ViewModels;

/// <summary>One navigation option's row on the Navkeys tab: its name and the key picked for it.</summary>
public sealed class NavKeyEditorItem : BaseViewModel
{
    /// <summary>The drop-down's entries: "(none)", then <see cref="NavKeyMap.Choices"/> in order.</summary>
    public static IReadOnlyList<string> KeyDisplays { get; } =
        ["(none)", .. NavKeyMap.Choices.Select(c => c.Display)];

    private int _keyIndex;

    public NavAction Action { get; }
    public string Label { get; }
    public string DefaultKey { get; }

    /// <summary>The picked entry of <see cref="KeyDisplays"/>. The Picker's -1 while it binds is read as none.</summary>
    public int KeyIndex
    {
        get => _keyIndex;
        set => Set(ref _keyIndex, Math.Max(0, value));
    }

    /// <summary>The picked key's stored name; empty for none.</summary>
    public string KeyName
    {
        get => _keyIndex <= 0 || _keyIndex > NavKeyMap.Choices.Count ? string.Empty : NavKeyMap.Choices[_keyIndex - 1].Name;
        set => KeyIndex = IndexOf(value);
    }

    public NavKeyEditorItem(NavActionInfo info, string keyName)
    {
        Action = info.Action;
        Label = info.Label;
        DefaultKey = info.DefaultKey;
        _keyIndex = IndexOf(keyName);
    }

    private static int IndexOf(string? keyName)
    {
        var choice = NavKeyMap.FindChoice(keyName);
        return choice is null ? 0 : NavKeyMap.Choices.ToList().IndexOf(choice) + 1;
    }
}
