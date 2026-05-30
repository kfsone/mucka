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
    /// The first word of <see cref="Name"/> — the persona name without title or level description.
    /// Used for identity matching so that a level-up (which changes the description) is not
    /// treated as a departure followed by a new arrival.
    /// </summary>
    public string PersonaName { get; private set; }

    /// <summary>Full display string as received from the server (e.g. "Ollie the Wizard").</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name       = value;
            PersonaName = FirstWord(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
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
        _name       = name;
        _color      = color;
        PersonaName = FirstWord(name);
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
