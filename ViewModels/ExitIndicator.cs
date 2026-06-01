using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Mucka.ViewModels;

/// <summary>
/// Tracks the presence or absence of a single room exit direction.
/// Color is bright green when present, muted red when absent.
/// FontAttributes is Bold when present, None when absent.
/// </summary>
public sealed class ExitIndicator : INotifyPropertyChanged
{
    private static readonly Color PresentColor = Color.FromArgb("#00ff00");
    private static readonly Color AbsentColor  = Color.FromArgb("#553333");

    private bool _present;

    public bool Present
    {
        get => _present;
        set
        {
            if (_present == value) return;
            _present = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Present)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FontAttributes)));
        }
    }

    public Color Color
        => _present ? PresentColor : AbsentColor;

    public FontAttributes FontAttributes
        => _present ? FontAttributes.Bold : FontAttributes.None;

    public event PropertyChangedEventHandler? PropertyChanged;
}
