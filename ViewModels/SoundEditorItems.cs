using System.Windows.Input;
using Mucka.Audio;

namespace Mucka.ViewModels;

/// <summary>One leaf in the Sounds tab's tree: a single sound effect with an on/off
/// checkbox, a tap-to-preview play button, and a volume slider that follows the group's
/// volume until the user moves it (dragging back onto the group value re-attaches it).</summary>
public sealed class SoundEditorItem : BaseViewModel
{
    private readonly int? _storedVolume;
    private bool _enabled;
    private double _volume;
    private bool _volumeOverridden;
    private bool _syncingVolume;

    public string Code { get; }
    public string Name { get; }
    public string AssetName { get; }
    /// <summary>The owning group — leaf rows bind IsEnabled to Group.Enabled so an
    /// unchecked group greys out its children.</summary>
    public SoundGroupEditorItem Group { get; private set; } = null!;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Playback volume percent (0–100). Inherits the group volume until the
    /// user moves the slider away from it.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetAndNotify(ref _volume, Math.Clamp(value, 0, 100), [nameof(VolumeDisplay)]))
                return;
            if (!_syncingVolume)
                _volumeOverridden = VolumeDisplay != Group.VolumeDisplay;
        }
    }
    public int VolumeDisplay => (int)Math.Round(_volume);
    /// <summary>True when the user has detached this sound's volume from its group's.</summary>
    public bool IsVolumeOverridden => _volumeOverridden;
    public ICommand PlayCommand { get; }

    public SoundEditorItem(string code, string name, string assetName, bool enabled, int? volume)
    {
        Code      = code;
        Name      = name;
        AssetName = assetName;
        _enabled  = enabled;
        _storedVolume = volume;
        // Preview at exactly the volume this row will play at.
        PlayCommand = new Command(() => SoundService.Play(AssetName, VolumeDisplay));
    }

    /// <summary>Called by the owning group's ctor once the inherited volume is known.
    /// A stored override equal to the group volume loads as inherited (re-attached).</summary>
    internal void AttachGroup(SoundGroupEditorItem group)
    {
        Group = group;
        _volumeOverridden = _storedVolume is int v && v != group.VolumeDisplay;
        _volume = _storedVolume ?? group.VolumeDisplay;
    }

    /// <summary>Follows the group's slider while not overridden.</summary>
    internal void OnGroupVolumeChanged(int groupVolume)
    {
        if (_volumeOverridden) return;
        _syncingVolume = true;
        try { Volume = groupVolume; }
        finally { _syncingVolume = false; }
    }

    internal void ResetVolumeToInherited(int groupVolume)
    {
        _volumeOverridden = false;
        _syncingVolume = true;
        try { Volume = groupVolume; }
        finally { _syncingVolume = false; }
    }
}

/// <summary>One collapsible group heading in the Sounds tab's tree: an on/off checkbox
/// that gates all children, a volume slider the children inherit (itself inheriting the
/// master volume until moved), plus a picker choosing the fallback sound played when the
/// server triggers a code in this group that has no wav of its own.</summary>
public sealed class SoundGroupEditorItem : BaseViewModel
{
    private readonly Func<int> _getMasterVolume;
    private bool _enabled;
    private bool _expanded;
    private int _selectedDefaultIndex;
    private double _volume;
    private bool _volumeOverridden;
    private bool _syncingVolume;

    public string Prefix { get; }
    public string Name { get; }
    public SoundEditorItem[] Sounds { get; }
    public bool HasChildren => Sounds.Length > 0;
    /// <summary>The fallback picker is only shown for catalogued clio groups (not the bell).</summary>
    public bool HasDefaultPicker { get; }
    /// <summary>"(none)" followed by the group's sound names; index aligns with Sounds + 1.</summary>
    public string[] DefaultChoices { get; }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Playback volume percent (0–100) inherited by the group's sounds.
    /// Inherits the master volume until the user moves the slider away from it;
    /// dragging back onto the master value re-attaches it.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetAndNotify(ref _volume, Math.Clamp(value, 0, 100), [nameof(VolumeDisplay)]))
                return;
            if (!_syncingVolume)
                _volumeOverridden = VolumeDisplay != _getMasterVolume();
            foreach (var sound in Sounds)
                sound.OnGroupVolumeChanged(VolumeDisplay);
        }
    }
    public int VolumeDisplay => (int)Math.Round(_volume);
    /// <summary>True when the user has detached this group's volume from the master's.</summary>
    public bool IsVolumeOverridden => _volumeOverridden;

    public bool Expanded
    {
        get => _expanded;
        set => SetAndNotify(ref _expanded, value, [nameof(Chevron)]);
    }
    public string Chevron => HasChildren ? (_expanded ? "▼" : "▶") : " ";

    /// <summary>Index into <see cref="DefaultChoices"/>; 0 = no fallback.</summary>
    public int SelectedDefaultIndex { get => _selectedDefaultIndex; set => Set(ref _selectedDefaultIndex, value); }
    /// <summary>The fallback sound's code, or null when "(none)" is selected.</summary>
    public string? SelectedDefaultCode
        => _selectedDefaultIndex > 0 && _selectedDefaultIndex <= Sounds.Length
            ? Sounds[_selectedDefaultIndex - 1].Code : null;

    public ICommand ToggleExpandCommand { get; }
    /// <summary>Preview for childless groups (the bell); null when the children carry it.</summary>
    public ICommand? PlayCommand { get; }
    public bool HasPreview => PlayCommand != null;

    public SoundGroupEditorItem(string prefix, string name, SoundEditorItem[] sounds,
        bool enabled, int? volume, Func<int> getMasterVolume,
        string? defaultCode, bool hasDefaultPicker, ICommand? playCommand = null)
    {
        PlayCommand = playCommand;
        Prefix  = prefix;
        Name    = name;
        Sounds  = sounds;
        _enabled = enabled;
        _getMasterVolume = getMasterVolume;
        HasDefaultPicker = hasDefaultPicker;

        // A stored override equal to the master volume loads as inherited (re-attached).
        var master = Math.Clamp(getMasterVolume(), 0, 100);
        _volumeOverridden = volume is int v && v != master;
        _volume = volume ?? master;
        foreach (var sound in sounds)
            sound.AttachGroup(this);

        DefaultChoices = new string[sounds.Length + 1];
        DefaultChoices[0] = "(none)";
        for (var i = 0; i < sounds.Length; i++)
            DefaultChoices[i + 1] = sounds[i].Name;
        _selectedDefaultIndex = defaultCode is null ? 0
            : Array.FindIndex(sounds, s => s.Code == defaultCode) + 1; // -1+1 = 0 = "(none)" when stale

        ToggleExpandCommand = new Command(() => Expanded = !Expanded);
    }

    /// <summary>Follows the master slider while not overridden.</summary>
    internal void OnMasterVolumeChanged(int masterVolume)
    {
        if (_volumeOverridden) return;
        _syncingVolume = true;
        try { Volume = masterVolume; }
        finally { _syncingVolume = false; }
    }

    /// <summary>Re-attaches the group and all its sounds to the inherited volume.</summary>
    internal void ResetVolumeToInherited()
    {
        _volumeOverridden = false;
        _syncingVolume = true;
        try { Volume = _getMasterVolume(); }
        finally { _syncingVolume = false; }
        // The Volume setter only cascades on change — reset the children explicitly so
        // their overrides clear even when the group's value didn't move.
        foreach (var sound in Sounds)
            sound.ResetVolumeToInherited(VolumeDisplay);
    }
}
