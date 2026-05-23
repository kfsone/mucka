using System.ComponentModel;

namespace Mucka.ViewModels;

/// <summary>A single F-key button entry bound in the GamePage fkey bar.</summary>
public sealed class FkeyItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _label;
    private double _width;

    public int    Index   { get; }
    public string Label   { get => _label; set { if (_label == value) return; _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); } }
    public double Width   { get => _width; set { if (_width == value) return; _width = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width))); } }
    public string Command { get; set; }

    public FkeyItem(int index, string command, bool showPrefix = true)
    {
        Index   = index;
        _label  = showPrefix ? $"F{index + 1}" : $"{index + 1}";
        Command = command;
    }
}
