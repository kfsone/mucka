using Microsoft.Maui.Graphics;
using System.ComponentModel;

namespace Mucka.ViewModels;

/// <summary>A single entry in the WHO list, carrying the player name and wire-protocol display color.</summary>
public sealed class WhoEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name  { get; }
    public Color  Color { get; }

    private double _opacity = 1.0;

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

    public WhoEntry(string name, Color color)
    {
        Name  = name;
        Color = color;
    }
}
