using Microsoft.Maui.Graphics;

namespace Mucka.ViewModels;

/// <summary>A single entry in the WHO list, carrying the player name and wire-protocol display c.</summary>
public sealed class WhoEntry
{
    public string Name  { get; }
    public Color  Color { get; }

    public WhoEntry(string name, Color color)
    {
        Name  = name;
        Color = color;
    }
}
