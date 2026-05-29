using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class FkeyEditorViewModel : BaseViewModel
{
    private readonly FkeyEditorItem[][] _pages;
    private readonly Action<string[]> _onApply;
    private readonly Func<string[], Task>? _onSave;
    private readonly Action<int>? _onColumnsChanged;
    private readonly Action<int>? _onFesChanged;
    private int _activeModifier;
    private int _activeTab = 1;
    private double _fontSize;
    private double _columns;
    private double _volume;
    private double _statUpdateFrequency;

    public int ActiveModifier
    {
        get => _activeModifier;
        set
        {
            if (Set(ref _activeModifier, value))
                OnPropertyChanged(nameof(CurrentPageItems));
        }
    }

    public int ActiveTab
    {
        get => _activeTab;
        set
        {
            if (Set(ref _activeTab, value))
                OnPropertiesChanged(nameof(IsSettingsTabActive), nameof(IsHotkeysTabActive), nameof(IsFriendsTabActive));
        }
    }

    public bool IsSettingsTabActive => _activeTab == 0;
    public bool IsHotkeysTabActive  => _activeTab == 1;
    public bool IsFriendsTabActive  => _activeTab == 2;

    public double FontSize
    {
        get => _fontSize;
        set => SetAndNotify(ref _fontSize, value, [nameof(FontSizeDisplay)]);
    }

    public double Columns
    {
        get => _columns;
        set => SetAndNotify(ref _columns, Math.Clamp(Math.Round(value), 40, 160), [nameof(ColumnsDisplay)]);
    }

    public double Volume
    {
        get => _volume;
        set => SetAndNotify(ref _volume, Math.Clamp(value, 0, 100), [nameof(VolumeDisplay)]);
    }

    public double StatUpdateFrequency
    {
        get => _statUpdateFrequency;
        set => SetAndNotify(ref _statUpdateFrequency, value, [nameof(StatUpdateFrequencyDisplay)]);
    }

    public int    FontSizeDisplay            => (int)Math.Round(_fontSize);
    public int    ColumnsDisplay             => (int)Math.Round(_columns);
    public int    VolumeDisplay              => (int)Math.Round(_volume);
    public string StatUpdateFrequencyDisplay => _statUpdateFrequency <= 0 ? "Off" : $"{(int)Math.Round(_statUpdateFrequency)}s";

    public FkeyEditorItem[] CurrentPageItems => _pages[_activeModifier];
    public bool CanSave { get; }

    public ICommand SetTabCommand { get; }
    public ICommand SetModifierCommand { get; }
    public ICommand ApplyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action? CloseRequested;

    public FkeyEditorViewModel(
        string[] allFkeys,
        double fontSize, double columns, double volume,
        double statUpdateFreq,
        Action<string[]> onApply,
        Func<string[], Task>? onSave,
        Action<int>? onColumnsChanged = null,
        Action<int>? onFesChanged = null)
    {
        _onApply = onApply;
        _onSave = onSave;
        _onColumnsChanged = onColumnsChanged;
        _onFesChanged = onFesChanged;
        CanSave = onSave != null;

        _fontSize = Math.Clamp(Math.Round(fontSize), 9, 24);
        _columns  = Math.Clamp(Math.Round(columns), 40, 160);
        _volume   = Math.Clamp(volume, 0, 100);
        _statUpdateFrequency = statUpdateFreq <= 0 ? 0 : Math.Clamp(Math.Round(statUpdateFreq / 5.0) * 5, 5, 30);

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

        SetTabCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var tab))
                ActiveTab = tab;
        });
        SetModifierCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var modifier))
                ActiveModifier = modifier;
        });
        ApplyCommand = new Command(() =>
        {
            ApplySettings();
            _onApply(CollectFkeys());
            CloseRequested?.Invoke();
        });
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        CancelCommand = new Command(() => CloseRequested?.Invoke());
    }

    private void ApplySettings()
    {
        _onColumnsChanged?.Invoke((int)Math.Round(_columns));
        _onFesChanged?.Invoke((int)Math.Round(_statUpdateFrequency));
    }

    private async Task SaveAsync()
    {
        ApplySettings();
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
            for (int k = 0; k < 12; k++)
                result[mod * 12 + k] = _pages[mod][k].Command ?? string.Empty;
        return result;
    }
}
