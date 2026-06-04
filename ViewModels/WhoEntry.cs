using Microsoft.Maui.Graphics;
using System.ComponentModel;

namespace Mucka.ViewModels;

/// <summary>A single entry in the WHO list, carrying the player name and wire-protocol display color.</summary>
public sealed class WhoEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name;
    private Color  _color;
    private double _opacity      = 1.0;
    private float  _glowProgress = 1.0f;  // 0 = peak glow (white), 1 = normal wire color

    /// <summary>
    /// The first word of <see cref="Name"/> — the persona name without title or level
    /// description, and without the invisibility parens. Used for identity matching so
    /// that a level-up (which changes the description) or an invisibility change (which
    /// wraps the whole name in parens) is not treated as a departure + new arrival.
    /// </summary>
    public string PersonaName { get; private set; }

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
            if (IsInvisible != wasInvisible)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInvisible)));
        }
    }

    /// <summary>Wire-protocol foreground color (mortal / wizard / etc.).</summary>
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

    /// <summary>
    /// The rendered text color: blends from white (glow peak) to <see cref="Color"/> (normal)
    /// as <see cref="GlowSince"/> elapses.  Bind the XAML label to this, not <see cref="Color"/>.
    /// </summary>
    public Color DisplayColor
    {
        get
        {
            if (_glowProgress >= 1.0f) return _color;
            var g = _glowProgress;
            return Color.FromRgba(
                1.0f - g + _color.Red   * g,
                1.0f - g + _color.Green * g,
                1.0f - g + _color.Blue  * g,
                1.0f);
        }
    }

    /// <summary>Current display opacity (1.0 = fully visible; animates toward 0 when departing).</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity == value) return;
            _opacity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity)));
        }
    }

    /// <summary>
    /// Set to the UTC time when the player was marked as having departed.
    /// Null means the player is present. Used by the fade timer to animate and remove the entry.
    /// </summary>
    public DateTime? DepartingSince { get; set; }

    /// <summary>
    /// Set to the UTC time when a glow was started (new arrival or updated name/level).
    /// Null means no glow is active.
    /// </summary>
    public DateTime? GlowSince { get; set; }

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

    /// <summary>
    /// Begin a glow animation (white → wire color over the configured duration).
    /// Call on the UI thread before adding to / after updating the list.
    /// </summary>
    internal void StartGlow()
    {
        GlowSince = DateTime.UtcNow;
        SetGlowProgress(0f);
    }

    /// <summary>Advance glow interpolation; called every timer tick.</summary>
    internal void SetGlowProgress(float progress)
    {
        var clamped = Math.Clamp(progress, 0f, 1f);
        if (Math.Abs(_glowProgress - clamped) < 0.004f) return;
        _glowProgress = clamped;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayColor)));
    }

    private static string FirstWord(string s)
    {
        var idx = s.IndexOf(' ');
        return idx > 0 ? s[..idx] : s;
    }
}
