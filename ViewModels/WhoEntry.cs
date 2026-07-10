using Microsoft.Maui.Graphics;
using System.ComponentModel;

namespace Mucka.ViewModels;

/// <summary>A single entry in the WHO list, carrying the player name and wire-protocol display color.</summary>
public sealed class WhoEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name;
    private Color  _color;

    /// <summary>
    /// The first word of <see cref="Name"/> — the persona name without title or level
    /// description, and without the invisibility parens. Used for identity matching so
    /// that a level-up (which changes the description) or an invisibility change (which
    /// wraps the whole name in parens) is not treated as a departure + new arrival.
    /// </summary>
    public string PersonaName { get; private set; }

    // Set by SidePanelViewModel when the NamesOnly display option changes.
    internal static bool NamesOnlyMode;

    /// <summary>
    /// True when the server sent the name wrapped in parens ("(Ollie the sorcerer)") —
    /// the player is invisible but we can still see them (it's us, a team member, or
    /// someone we outrank). A status, not part of the name.
    /// </summary>
    public bool IsInvisible { get; private set; }

    /// <summary>Full display string as received from the server (e.g. "Ollie the Wizard",
    /// "(Ollie the Wizard)" while invisible). Parens are kept for display — they are the
    /// game's own invisibility convention.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            var wasInvisible = IsInvisible;
            SetIdentityFrom(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySuffix)));
            if (IsInvisible != wasInvisible)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInvisible)));
        }
    }

    /// <summary>The wire-protocol foreground color (mortal / wizard / etc.).</summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayColor)));
        }
    }

    /// <summary>The text color the XAML label binds to. (Was a glow-blend driven by the old
    /// UI-thread fade timer; arrival/departure animation is now a GPU compositor fade — see
    /// <c>WhoEntryFadeBehavior</c> — so this is just the wire color.)</summary>
    public Color DisplayColor => _color;

    /// <summary>Persona name portion for display — the first word of <see cref="Name"/>,
    /// prefixed with "(" when the player is invisible. Bound to the full-size span in the
    /// who-list template; suffix is rendered 2pt smaller via <see cref="DisplaySuffix"/>.</summary>
    public string DisplayName => IsInvisible ? "(" + PersonaName : PersonaName;

    /// <summary>Title/level description that follows the persona name (e.g. " the Wizard"),
    /// with a closing ")" appended when the player is invisible. May be empty for untitled
    /// players. Rendered 2pt smaller than <see cref="DisplayName"/>.</summary>
    public string DisplaySuffix
    {
        get
        {
            if (NamesOnlyMode) return string.Empty;
            var inner = IsInvisible ? _name[1..^1] : _name;
            var idx   = inner.IndexOf(' ');
            var tail  = idx >= 0 ? inner[idx..] : string.Empty;
            return IsInvisible ? tail + ")" : tail;
        }
    }

    /// <summary>Forces a PropertyChanged for the suffix so NamesOnly mode changes propagate.</summary>
    public void NotifyDisplaySuffixChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySuffix)));

    private bool _isDeparting;
    /// <summary>True once the player has left: the entry stays in the list while
    /// <c>WhoEntryFadeBehavior</c> runs a GPU fade-out, then the view-model removes it. Reset to
    /// false (cancelling the fade and the pending removal) if the player reappears in time.</summary>
    public bool IsDeparting
    {
        get => _isDeparting;
        set
        {
            if (_isDeparting == value) return;
            _isDeparting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeparting)));
        }
    }

    /// <summary>UTC time this player was last seen in a FEW snapshot. Drives the age/grouping of a
    /// Recent-list entry; unused while the entry is live in the Online list.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>UTC time a Recent-list entry should be forgotten (removed). Unused while online.</summary>
    public DateTime ExpiryUtc { get; set; }

    public WhoEntry(string name, Color color)
    {
        _name  = name;
        _color = color;
        PersonaName = "";   // assigned by SetIdentityFrom
        SetIdentityFrom(name);
    }

    // Derive identity (PersonaName) and visibility status (IsInvisible) from the wire name.
    private void SetIdentityFrom(string name)
    {
        IsInvisible = name.Length >= 2 && name[0] == '(' && name[^1] == ')';
        PersonaName = FirstWord(IsInvisible ? name[1..^1] : name);
    }

    private static string FirstWord(string s)
    {
        var idx = s.IndexOf(' ');
        return idx > 0 ? s[..idx] : s;
    }
}
