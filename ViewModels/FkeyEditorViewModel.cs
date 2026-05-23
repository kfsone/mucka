using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class FkeyEditorViewModel : BaseViewModel
{
    private readonly FkeyEditorItem[][] _pages;
    private readonly Action<string[]> _onApply;
    private readonly Func<string[], Task>? _onSave;
    private int _activeModifier;

    public int ActiveModifier
    {
        get => _activeModifier;
        set
        {
            if (Set(ref _activeModifier, value))
                OnPropertyChanged(nameof(CurrentPageItems));
        }
    }

    public FkeyEditorItem[] CurrentPageItems => _pages[_activeModifier];
    public bool CanSave { get; }

    public ICommand SetModifierCommand { get; }
    public ICommand ApplyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action? CloseRequested;

    public FkeyEditorViewModel(string[] allFkeys, Action<string[]> onApply, Func<string[], Task>? onSave)
    {
        _onApply = onApply;
        _onSave = onSave;
        CanSave = onSave != null;

        var fkeys = new string[36];
        for (int i = 0; i < 36; i++)
            fkeys[i] = i < allFkeys.Length ? allFkeys[i] ?? string.Empty : string.Empty;

        _pages = new FkeyEditorItem[3][];
        for (int mod = 0; mod < 3; mod++)
        {
            _pages[mod] = new FkeyEditorItem[12];
            for (int k = 0; k < 12; k++)
                _pages[mod][k] = new FkeyEditorItem(mod * 12 + k, k + 1, fkeys[mod * 12 + k]);
        }

        SetModifierCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var modifier))
                ActiveModifier = modifier;
        });
        ApplyCommand = new Command(() =>
        {
            _onApply(CollectFkeys());
            CloseRequested?.Invoke();
        });
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        CancelCommand = new Command(() => CloseRequested?.Invoke());
    }

    private async Task SaveAsync()
    {
        var fkeys = CollectFkeys();
        if (_onSave != null)
            await _onSave(fkeys);
        else
            _onApply(fkeys);
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Populates all editor fields from an imported fkeys array (e.g. from clio.ini).
    /// Shorter arrays are padded with empty strings; existing entries beyond the provided
    /// length are cleared to empty.
    /// </summary>
    public void ImportFkeys(string[] fkeys)
    {
        for (int mod = 0; mod < 3; mod++)
            for (int k = 0; k < 12; k++)
            {
                int idx = mod * 12 + k;
                _pages[mod][k].Command = idx < fkeys.Length ? fkeys[idx] ?? string.Empty : string.Empty;
            }
        OnPropertyChanged(nameof(CurrentPageItems));
    }

    private string[] CollectFkeys()
    {
        var result = new string[36];
        for (int mod = 0; mod < 3; mod++)
        {
            for (int k = 0; k < 12; k++)
                result[mod * 12 + k] = _pages[mod][k].Command ?? string.Empty;
        }
        return result;
    }
}
