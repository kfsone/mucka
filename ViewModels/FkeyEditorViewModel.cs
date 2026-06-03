using System.Windows.Input;
using Mucka.Audio;
using Mucka.Core;

namespace Mucka.ViewModels;

public sealed class FkeyEditorViewModel : BaseViewModel
{
    private readonly FkeyEditorItem[][] _pages;
    private readonly ClientSettings _original;
    private readonly Action<ClientSettings, string[]> _onApply;
    private readonly Func<ClientSettings, string[], Task>? _onSave;
    private int _activeModifier;
    private int _activeTab = 1;
    private double _fontSize;
    private double _columns;
    private double _volume;
    private double _statUpdateFrequency;
    private bool _muteBeepSession;
    private bool _muteBeepPermanently;
    private bool _settingsToProfileOnly;
    private bool _fkeysToProfileOnly;

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

    public bool MuteBeepSession
    {
        get => _muteBeepSession;
        set
        {
            if (Set(ref _muteBeepSession, value) && !value)
                MuteBeepPermanently = false;
        }
    }

    public bool MuteBeepPermanently
    {
        get => _muteBeepPermanently;
        set => Set(ref _muteBeepPermanently, value);
    }

    /// <summary>Settings page's "Save to profile only": save to [settings:Name] instead of [settings].</summary>
    public bool SettingsToProfileOnly
    {
        get => _settingsToProfileOnly;
        set => Set(ref _settingsToProfileOnly, value);
    }

    /// <summary>Hotkeys page's "Save to profile only": save to [fkeys:Name] instead of [fkeys].</summary>
    public bool FkeysToProfileOnly
    {
        get => _fkeysToProfileOnly;
        set => Set(ref _fkeysToProfileOnly, value);
    }

    public FkeyEditorItem[] CurrentPageItems => _pages[_activeModifier];
    public bool CanSave { get; }

    public ICommand SetTabCommand { get; }
    public ICommand SetModifierCommand { get; }
    public ICommand ApplyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PlayBeepCommand { get; }

    public event Action? CloseRequested;
    /// <summary>Raised when Save fails; payload is the error message for display.</summary>
    public event Action<string>? SaveFailed;

    public FkeyEditorViewModel(
        string[] allFkeys,
        ClientSettings settings,
        Action<ClientSettings, string[]> onApply,
        Func<ClientSettings, string[], Task>? onSave)
    {
        _original = settings;
        _onApply  = onApply;
        _onSave   = onSave;
        CanSave   = onSave != null;

        _fontSize = Math.Clamp(settings.FontSize, 9, 24);
        _columns  = Math.Clamp(settings.MaxColumns, 40, 160);
        _volume   = Math.Clamp(settings.Volume, 0, 100);
        _statUpdateFrequency = settings.StatUpdateFrequency <= 0
            ? 0 : Math.Clamp(Math.Round(settings.StatUpdateFrequency / 5.0) * 5, 5, 30);
        _muteBeepSession     = settings.MuteBeepSession;
        _muteBeepPermanently = settings.MuteBeepPermanently;
        _settingsToProfileOnly = settings.SettingsPerProfile;
        _fkeysToProfileOnly    = settings.FkeysPerProfile;

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
            _onApply(CollectSettings(), CollectFkeys());
            CloseRequested?.Invoke();
        });
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        CancelCommand = new Command(() =>
        {
            // Undo any preview-volume change from the "hear it" link.
            SoundService.SetVolume(_original.Volume);
            CloseRequested?.Invoke();
        });
        // Preview the beep at the volume currently on the slider.
        PlayBeepCommand = new Command(() =>
        {
            SoundService.SetVolume(VolumeDisplay);
            SoundService.Play("beep.wav");
        });
    }

    /// <summary>The edited settings as a snapshot for apply/save.</summary>
    private ClientSettings CollectSettings() => new()
    {
        FontSize            = FontSizeDisplay,
        MaxColumns          = ColumnsDisplay,
        Volume              = VolumeDisplay,
        StatUpdateFrequency = (int)Math.Round(_statUpdateFrequency),
        MuteBeepSession     = _muteBeepSession,
        MuteBeepPermanently = _muteBeepPermanently,
        SettingsPerProfile  = _settingsToProfileOnly,
        FkeysPerProfile     = _fkeysToProfileOnly,
    };

    private async Task SaveAsync()
    {
        try
        {
            // The saver applies the settings live and persists them (GameViewModel.SaveSettingsAsync).
            await _onSave!(CollectSettings(), CollectFkeys());
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            // Stay open so the user can retry or cancel; the page shows the error.
            SaveFailed?.Invoke(ex.Message);
        }
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
