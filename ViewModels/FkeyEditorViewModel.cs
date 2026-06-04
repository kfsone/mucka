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
    private bool _soundsEnabled;
    private readonly SoundGroupEditorItem _bellGroup;

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
                OnPropertiesChanged(nameof(IsSettingsTabActive), nameof(IsHotkeysTabActive),
                    nameof(IsSoundsTabActive), nameof(IsFriendsTabActive));
        }
    }

    public bool IsSettingsTabActive => _activeTab == 0;
    public bool IsHotkeysTabActive  => _activeTab == 1;
    public bool IsSoundsTabActive   => _activeTab == 2;
    public bool IsFriendsTabActive  => _activeTab == 3;

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
        set
        {
            if (!SetAndNotify(ref _volume, Math.Clamp(value, 0, 100), [nameof(VolumeDisplay)]))
                return;
            // Non-overridden group sliders (and their sounds) follow the master.
            if (SoundGroups is not null)
                foreach (var group in SoundGroups)
                    group.OnMasterVolumeChanged(VolumeDisplay);
        }
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
            if (Set(ref _muteBeepSession, value))
            {
                if (!value)
                    MuteBeepPermanently = false;
                SyncBellFromMute();
            }
        }
    }

    public bool MuteBeepPermanently
    {
        get => _muteBeepPermanently;
        set
        {
            if (Set(ref _muteBeepPermanently, value))
                SyncBellFromMute();
        }
    }

    /// <summary>Master sound switch — the Sounds tab's top-level checkbox.</summary>
    public bool SoundsEnabled
    {
        get => _soundsEnabled;
        set => Set(ref _soundsEnabled, value);
    }

    /// <summary>The Sounds tab's tree: catalogued clio groups plus the bell row.</summary>
    public SoundGroupEditorItem[] SoundGroups { get; }

    // The bell row and the Settings tab's mute checkboxes are two views of one setting:
    // bell unchecked ⇔ both mute flags set (Apply mutes the session, Save persists it).
    // _syncingBell suppresses the bell row's change handler while the mute flags push
    // into it, so a session-only mute doesn't bounce back as a permanent one.
    private bool _syncingBell;

    private void SyncBellFromMute()
    {
        _syncingBell = true;
        try { _bellGroup.Enabled = !(_muteBeepSession || _muteBeepPermanently); }
        finally { _syncingBell = false; }
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
    public ICommand ResetSoundsCommand { get; }

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
        _soundsEnabled         = settings.Sounds.MasterEnabled;

        // Preview the beep at the bell row's volume (which follows the master slider
        // until overridden). _bellGroup is read at invoke time — it doesn't exist yet here.
        PlayBeepCommand = new Command(() =>
            SoundService.Play("beep.wav", _bellGroup?.VolumeDisplay ?? VolumeDisplay));

        // Sounds tab tree: one collapsible item per catalogued clio group, plus the bell.
        var groups = new List<SoundGroupEditorItem>(SoundCatalog.Groups.Length + 1);
        foreach (var g in SoundCatalog.Groups)
        {
            var leaves = new SoundEditorItem[g.Sounds.Length];
            for (var i = 0; i < g.Sounds.Length; i++)
            {
                var s = g.Sounds[i];
                leaves[i] = new SoundEditorItem(s.Code, s.Name, s.AssetName,
                    settings.Sounds.IsSoundEnabled(s.Code),
                    settings.Sounds.GetSoundVolume(s.Code));
            }
            groups.Add(new SoundGroupEditorItem(g.Prefix, g.Name, leaves,
                settings.Sounds.IsGroupEnabled(g.Prefix),
                settings.Sounds.GetGroupVolume(g.Prefix), () => VolumeDisplay,
                settings.Sounds.GetGroupDefault(g.Prefix),
                hasDefaultPicker: true));
        }
        _bellGroup = new SoundGroupEditorItem(SoundSettings.BellGroup, "Terminal bell (beep)",
            Array.Empty<SoundEditorItem>(),
            enabled: !(_muteBeepSession || _muteBeepPermanently),
            volume: settings.Sounds.GetGroupVolume(SoundSettings.BellGroup), () => VolumeDisplay,
            defaultCode: null, hasDefaultPicker: false, playCommand: PlayBeepCommand);
        _bellGroup.PropertyChanged += (_, e) =>
        {
            // Route user toggles of the bell row into the mute flags (see SyncBellFromMute).
            if (e.PropertyName == nameof(SoundGroupEditorItem.Enabled) && !_syncingBell)
                MuteBeepSession = MuteBeepPermanently = !_bellGroup.Enabled;
        };
        groups.Add(_bellGroup);
        SoundGroups = groups.ToArray();

        ResetSoundsCommand = new Command(() =>
        {
            SoundsEnabled = true;
            Volume = 75;
            foreach (var group in SoundGroups)
            {
                group.Enabled = true;             // the bell row also clears the mute flags
                group.ResetVolumeToInherited();   // group and sounds re-attach to the master
                group.SelectedDefaultIndex = 0;
                foreach (var sound in group.Sounds)
                    sound.Enabled = true;
            }
        });

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
        Sounds              = CollectSounds(),
    };

    /// <summary>The Sounds tab's tree as an override-only settings blob.</summary>
    private SoundSettings CollectSounds()
    {
        var sounds = new SoundSettings { MasterEnabled = _soundsEnabled };
        foreach (var group in SoundGroups)
        {
            if (group.IsVolumeOverridden)
                sounds.GroupVolumes[group.Prefix] = group.VolumeDisplay;
            if (group.Prefix == SoundSettings.BellGroup)
                continue; // the bell's on/off persists via MuteBeep*; only its volume above
            if (!group.Enabled)
                sounds.DisabledGroups.Add(group.Prefix);
            if (group.SelectedDefaultCode is { } code)
                sounds.GroupDefaults[group.Prefix] = code;
            foreach (var sound in group.Sounds)
            {
                if (!sound.Enabled)
                    sounds.DisabledSounds.Add(sound.Code);
                if (sound.IsVolumeOverridden)
                    sounds.SoundVolumes[sound.Code] = sound.VolumeDisplay;
            }
        }
        return sounds;
    }

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
