namespace Mucka.ViewModels;

/// <summary>A single F-key button entry bound in the GamePage fkey bar.</summary>
public sealed class FkeyItem
{
    public int Index { get; }
    public string Label { get; }
    public string Command { get; set; }

    public FkeyItem(int index, string command)
    {
        Index = index;
        Label = $"F{index + 1}";
        Command = command;
    }
}
