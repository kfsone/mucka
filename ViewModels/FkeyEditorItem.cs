namespace Mucka.ViewModels;

public sealed class FkeyEditorItem : BaseViewModel
{
    private string _command = string.Empty;

    public int AbsoluteIndex { get; }
    public string Label { get; }
    public string Command { get => _command; set => Set(ref _command, value); }

    public FkeyEditorItem(int absoluteIndex, int functionNumber, string command)
    {
        AbsoluteIndex = absoluteIndex;
        Label = $"F{functionNumber}";
        _command = command ?? string.Empty;
    }
}
