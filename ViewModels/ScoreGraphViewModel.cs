using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Mucka.Combat;
using Mucka.Commands;
using Mucka.Core;
using Mucka.Rendering;

namespace Mucka.ViewModels;

/// <summary>One row in the score panel's server or character list.</summary>
public sealed class ScoreChoice : BaseViewModel
{
    private bool _isChecked;

    public ScoreChoice(string label, string host, PersonaKey? persona, bool isChecked)
    {
        Label = label;
        Host = host;
        Persona = persona;
        _isChecked = isChecked;
        ToggleCommand = new Command(() => IsChecked = !IsChecked);
    }

    public ICommand ToggleCommand { get; }

    public string Label { get; }
    public string Host { get; }
    /// <summary>Null for a server row.</summary>
    public PersonaKey? Persona { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetAndNotify(ref _isChecked, value, [nameof(Box)]);
    }

    /// <summary>A text glyph rather than a CheckBox: WinUI's CheckBox carries a wide minimum width
    /// that parts the box from its label.</summary>
    public string Box => _isChecked ? Glyph.BoxChecked : Glyph.BoxEmpty;
}

/// <summary>One of the score panel's scales: every reading, or buckets of a fixed length.</summary>
public sealed class ScoreScaleChoice : BaseViewModel
{
    private bool _isSelected;

    public ScoreScaleChoice(string label, long bucketMs, Action<ScoreScaleChoice> select)
    {
        Label = label;
        BucketMs = bucketMs;
        SelectCommand = new Command(() => select(this));
    }

    public string Label { get; }
    public long BucketMs { get; }
    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetAndNotify(ref _isSelected, value, [nameof(Radio)]);
    }

    public string Radio => _isSelected ? Glyph.RadioOn : Glyph.RadioOff;
}

/// <summary>One character's line in the score panel's summary table. <see cref="Net"/> is gained less
/// lost, which is also where the window closed less where it opened.</summary>
public sealed record ScoreStatRow(string Label, Color Color, string Start, string Low, string High,
    string Gained, string Lost, string Net, Color NetColor);

/// <summary>
/// The $SCORE panel. Reads the whole <see cref="ScoreCommandArgs.MaxDays"/> window once per open, off
/// the UI thread, and answers every control - servers, characters, days, scale, squeeze, zero - from that
/// copy. No live updates while it is open.
/// </summary>
public sealed class ScoreGraphViewModel : BaseViewModel
{
    private readonly Func<string> _databasePath;
    private readonly Func<long> _runId;
    private readonly Func<string> _currentHost;
    private readonly Func<string?> _currentPersona;
    private readonly Action _requestFocus;

    private ScoreHistory _history = ScoreHistory.Empty;
    private int _loadGeneration;
    private bool _rebuilding;
    // A read failure or an unknown requested persona: shown instead of the computed status until the
    // selection changes.
    private string? _loadStatus;

    private bool _isVisible;
    private bool _isServersOpen;
    private bool _isCharactersOpen;
    private bool _isScaleOpen;
    private int _days = ScoreCommandArgs.DefaultDays;
    private bool _isSqueezed;
    private ScoreScaleChoice _scale;
    private ScoreGraphScene? _scene;
    private string _status = string.Empty;

    public ScoreGraphViewModel(Func<string> databasePath, Func<long> runId, Func<string> currentHost,
        Func<string?> currentPersona, Action requestFocus)
    {
        _databasePath = databasePath;
        _runId = runId;
        _currentHost = currentHost;
        _currentPersona = currentPersona;
        _requestFocus = requestFocus;

        Scales =
        [
            new("raw", 0, SelectScale),
            new("1 minute", (long)TimeSpan.FromMinutes(1).TotalMilliseconds, SelectScale),
            new("1 hour", (long)TimeSpan.FromHours(1).TotalMilliseconds, SelectScale),
            new("1 day", (long)TimeSpan.FromDays(1).TotalMilliseconds, SelectScale),
        ];
        _scale = Scales[0];
        _scale.IsSelected = true;

        CloseCommand = new Command(Close);
        ToggleServersCommand = new Command(() => OpenList(servers: !IsServersOpen));
        ToggleCharactersCommand = new Command(() => OpenList(characters: !IsCharactersOpen));
        ToggleScaleCommand = new Command(() => OpenList(scale: !IsScaleOpen));
        CloseListsCommand = new Command(() => OpenList());
        ToggleSqueezeCommand = new Command(() =>
        {
            IsSqueezed = !IsSqueezed;
            _requestFocus();
        });
        ToggleZeroCommand = new Command(() =>
        {
            IsZero = !IsZero;
            _requestFocus();
        });
    }

    public ObservableCollection<ScoreChoice> Servers { get; } = [];
    public ObservableCollection<ScoreChoice> Characters { get; } = [];
    public IReadOnlyList<ScoreScaleChoice> Scales { get; }
    public ObservableCollection<ScoreStatRow> Stats { get; } = [];

    public ICommand CloseCommand { get; }
    public ICommand ToggleServersCommand { get; }
    public ICommand ToggleCharactersCommand { get; }
    public ICommand ToggleScaleCommand { get; }
    public ICommand CloseListsCommand { get; }
    public ICommand ToggleSqueezeCommand { get; }
    public ICommand ToggleZeroCommand { get; }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    public bool IsServersOpen
    {
        get => _isServersOpen;
        private set => SetAndNotify(ref _isServersOpen, value, [nameof(IsAnyListOpen)]);
    }

    public bool IsCharactersOpen
    {
        get => _isCharactersOpen;
        private set => SetAndNotify(ref _isCharactersOpen, value, [nameof(IsAnyListOpen)]);
    }

    public bool IsScaleOpen
    {
        get => _isScaleOpen;
        private set => SetAndNotify(ref _isScaleOpen, value, [nameof(IsAnyListOpen)]);
    }

    public bool IsAnyListOpen => _isServersOpen || _isCharactersOpen || _isScaleOpen;

    /// <summary>The slider's top end, as the double Slider.Maximum takes.</summary>
    public static double MaxDays => ScoreCommandArgs.MaxDays;

    /// <summary>The slider's value. Rounded to whole days; only a change of day rebuilds.</summary>
    public double DaysValue
    {
        get => _days;
        set
        {
            var days = Math.Clamp((int)Math.Round(value), 1, ScoreCommandArgs.MaxDays);
            if (days == _days)
                return;
            _days = days;
            OnPropertiesChanged(nameof(DaysValue), nameof(DaysLabel));
            RebuildScene();
        }
    }

    public string DaysLabel => _days == 1 ? "1 day" : $"{_days.ToString(CultureInfo.InvariantCulture)} days";

    public bool IsSqueezed
    {
        get => _isSqueezed;
        set
        {
            if (SetAndNotify(ref _isSqueezed, value, [nameof(SqueezeBox)]))
                RebuildScene();
        }
    }

    public string SqueezeBox => _isSqueezed ? Glyph.BoxChecked : Glyph.BoxEmpty;

    private bool _isZero;

    /// <summary>The Y axis runs down to zero instead of fitting the visible scores.</summary>
    public bool IsZero
    {
        get => _isZero;
        set
        {
            if (SetAndNotify(ref _isZero, value, [nameof(ZeroBox)]))
                RebuildScene();
        }
    }

    public string ZeroBox => _isZero ? Glyph.BoxChecked : Glyph.BoxEmpty;

    public ScoreGraphScene? Scene
    {
        get => _scene;
        private set => Set(ref _scene, value);
    }

    public string Status
    {
        get => _status;
        private set => SetAndNotify(ref _status, value, [nameof(HasStatus)]);
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>The drop-downs' current values; the panel supplies each caption and chevron.</summary>
    public string ServersSummary => Summary(Servers, "servers");
    public string CharactersSummary => Summary(Characters, "characters");
    public string ScaleSummary => _scale.Label;

    public const double DefaultWidth = 760;
    public const double DefaultHeight = 400;
    /// <summary>Wide enough for the summary table's fixed columns (616) inside the panel's padding.</summary>
    public const double MinWidth = 660;
    public const double MinHeight = 320;
    // Global [settings] keys: one size for every profile, like the other panel preferences.
    private const string WidthKey = "scorepanelwidth";
    private const string HeightKey = "scorepanelheight";

    private double _panelWidth = DefaultWidth;
    private double _panelHeight = DefaultHeight;
    private bool _sizeLoaded;
    private (string W, string H)? _savedSize;
    // True from Open until its read lands: the lists and history still hold the previous open's
    // data, so nothing is rebuilt from them in between.
    private bool _loading;
    // What the summary table was last built from; a scale, squeeze or zero change leaves it alone.
    private (long Start, long End, string Keys)? _statsKey;

    public double PanelWidth
    {
        get => _panelWidth;
        private set => Set(ref _panelWidth, value);
    }

    public double PanelHeight
    {
        get => _panelHeight;
        private set => Set(ref _panelHeight, value);
    }

    /// <summary>Sizes the panel, no smaller than the minimum and no larger than
    /// <paramref name="maxWidth"/> by <paramref name="maxHeight"/> - the room the page has for it.
    /// Where the room is smaller than the minimum (a phone), the room wins, so the close button stays
    /// on screen.</summary>
    public void Resize(double width, double height, double maxWidth, double maxHeight)
    {
        PanelWidth = Math.Min(Math.Max(width, MinWidth), Math.Max(1, maxWidth));
        PanelHeight = Math.Min(Math.Max(height, MinHeight), Math.Max(1, maxHeight));
    }

    /// <summary>A resize drag has ended: keep the size, and hand the keyboard back.</summary>
    public void EndResize()
    {
        SaveSize();
        _requestFocus();
    }

    /// <summary>Remembers the current size for every later session.</summary>
    private void SaveSize()
    {
        var w = Math.Round(_panelWidth).ToString(CultureInfo.InvariantCulture);
        var h = Math.Round(_panelHeight).ToString(CultureInfo.InvariantCulture);
        if ((w, h) == _savedSize)
            return;
        _savedSize = (w, h);
        _ = Task.Run(async () =>
        {
            try
            {
                await SettingsStore.SetGlobalValueAsync(WidthKey, w).ConfigureAwait(false);
                await SettingsStore.SetGlobalValueAsync(HeightKey, h).ConfigureAwait(false);
            }
            catch (IOException ex) { CrashLog.Write("ScoreGraphViewModel.SaveSize", ex); }
            catch (UnauthorizedAccessException ex) { CrashLog.Write("ScoreGraphViewModel.SaveSize", ex); }
        });
    }

    /// <summary>Reads the remembered size once, off the UI thread. A missing or unreadable value
    /// leaves the default.</summary>
    private void LoadSizeOnce()
    {
        if (_sizeLoaded) return;
        _sizeLoaded = true;
        _ = Task.Run(async () =>
        {
            string? w = null, h = null;
            try
            {
                w = await SettingsStore.GetGlobalValueAsync(WidthKey).ConfigureAwait(false);
                h = await SettingsStore.GetGlobalValueAsync(HeightKey).ConfigureAwait(false);
            }
            catch (IOException ex) { CrashLog.Write("ScoreGraphViewModel.LoadSize", ex); }
            catch (UnauthorizedAccessException ex) { CrashLog.Write("ScoreGraphViewModel.LoadSize", ex); }
            if (!double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                return;
            // The page clamps again on its next resize; here only the floor is known.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PanelWidth = Math.Max(MinWidth, width);
                PanelHeight = Math.Max(MinHeight, height);
            });
        });
    }

    /// <summary>Opens the panel - or re-aims it when already open - and re-reads the database.</summary>
    public void Open(ScoreCommandArgs args)
    {
        LoadSizeOnce();
        _days = args.Days;
        OnPropertiesChanged(nameof(DaysValue), nameof(DaysLabel));
        OpenList();
        IsVisible = true;
        _loadStatus = null;
        Status = "Loading...";
        _loading = true;
        _statsKey = null;
        Scene = null;
        Stats.Clear();

        var generation = ++_loadGeneration;
        var path = _databasePath();
        var runId = _runId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var since = now - (long)TimeSpan.FromDays(ScoreCommandArgs.MaxDays).TotalMilliseconds;
        _ = Task.Run(() =>
        {
            ScoreHistory history;
            string? failure = null;
            try
            {
                history = ScoreHistoryReader.Read(path, since, now, runId);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                CrashLog.Write("ScoreGraphViewModel.Open", ex);
                history = ScoreHistory.Empty;
                failure = "Could not read the score history: " + ex.Message;
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (generation != _loadGeneration || !IsVisible)
                    return;
                Loaded(history, args.Persona, failure);
            });
        });
    }

    /// <summary>True when the panel was open and is now closed.</summary>
    /// <param name="refocus">False when something else is about to take the keyboard - the persona
    /// picker, when leaving the game raises it - so it is not the command box's to be handed.</param>
    public bool TryClose(bool refocus = true)
    {
        if (!IsVisible)
            return false;
        Close(refocus);
        return true;
    }

    private void Close() => Close(refocus: true);

    private void Close(bool refocus)
    {
        IsServersOpen = false;
        IsCharactersOpen = false;
        IsScaleOpen = false;
        if (refocus)
            _requestFocus();
        IsVisible = false;
        Scene = null;
        Stats.Clear();
        _statsKey = null;
        Closed?.Invoke();
    }

    /// <summary>Raised when the panel closes, by its x or <see cref="TryClose"/>. A page that exists
    /// only to show the panel closes itself on it.</summary>
    public event Action? Closed;

    /// <summary>Opens at most one of the three drop-down lists, and hands the keyboard back.</summary>
    private void OpenList(bool servers = false, bool characters = false, bool scale = false)
    {
        IsServersOpen = servers;
        IsCharactersOpen = characters;
        IsScaleOpen = scale;
        _requestFocus();
    }

    private void SelectScale(ScoreScaleChoice choice)
    {
        _scale.IsSelected = false;
        _scale = choice;
        choice.IsSelected = true;
        OnPropertyChanged(nameof(ScaleSummary));
        OpenList();
        RebuildScene();
    }

    private void Loaded(ScoreHistory history, string? requestedPersona, string? failure)
    {
        _loading = false;
        _history = history;
        var currentHost = _currentHost().Trim();
        var persona = requestedPersona ?? _currentPersona();

        // A requested persona not on the current server enables whichever server it is on.
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentHost };
        PersonaKey[] wanted = [];
        if (persona is { Length: > 0 })
        {
            wanted = PersonasOn(enabled).Where(k => NameIs(k, persona)).ToArray();
            if (wanted.Length == 0)
            {
                wanted = history.Points.Keys.Where(k => NameIs(k, persona)).ToArray();
                foreach (var k in wanted)
                    enabled.Add(k.Host);
            }
        }
        else
        {
            // Nobody logged in and nobody named: the character played most recently on this server.
            var recent = PersonasOn(enabled).FirstOrDefault();
            if (recent != default)
                wanted = [recent];
        }

        _rebuilding = true;
        Replace(Servers, history.Hosts().Select(h => new ScoreChoice(h, h, null, enabled.Contains(h))));
        _rebuilding = false;
        RebuildCharacters(wanted.ToHashSet());

        _loadStatus = failure
            ?? (persona is { Length: > 0 } && wanted.Length == 0
                ? $"No score recorded for {persona} in the last {ScoreCommandArgs.MaxDays} days."
                : null);
        RebuildScene();
    }

    private static bool NameIs(PersonaKey key, string name)
        => string.Equals(key.Persona, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Characters with score readings on the given servers, most recently scored first.</summary>
    private IEnumerable<PersonaKey> PersonasOn(HashSet<string> hosts)
        => _history.Points
            .Where(kv => hosts.Contains(kv.Key.Host) && kv.Value.Length > 0)
            .OrderByDescending(kv => kv.Value[^1].Ms)
            .Select(kv => kv.Key);

    private HashSet<string> CheckedHosts()
        => new(Servers.Where(s => s.IsChecked).Select(s => s.Host), StringComparer.OrdinalIgnoreCase);

    private void RebuildCharacters(HashSet<PersonaKey> checkedKeys)
    {
        var keys = PersonasOn(CheckedHosts()).ToList();
        var ambiguous = keys.GroupBy(k => k.Persona).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        _rebuilding = true;
        Replace(Characters, keys.Select(k => new ScoreChoice(
            ambiguous.Contains(k.Persona) ? $"{k.Persona} ({k.Host})" : k.Persona,
            k.Host, k, checkedKeys.Contains(k))));
        _rebuilding = false;
    }

    private void Replace(ObservableCollection<ScoreChoice> list, IEnumerable<ScoreChoice> items)
    {
        foreach (var old in list)
            old.PropertyChanged -= OnChoiceChanged;
        list.Clear();
        foreach (var item in items)
        {
            item.PropertyChanged += OnChoiceChanged;
            list.Add(item);
        }
        OnPropertiesChanged(nameof(ServersSummary), nameof(CharactersSummary));
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rebuilding || e.PropertyName != nameof(ScoreChoice.IsChecked) || sender is not ScoreChoice choice)
            return;
        // The load's message answered the request that opened the panel; a change of selection is a
        // new question.
        _loadStatus = null;
        if (choice.Persona is null)
            RebuildCharacters(Characters.Where(c => c.IsChecked && c.Persona is not null).Select(c => c.Persona!.Value).ToHashSet());
        OnPropertiesChanged(nameof(ServersSummary), nameof(CharactersSummary));
        RebuildScene();
        _requestFocus();
    }

    private void RebuildScene()
    {
        if (!IsVisible)
        {
            Scene = null;
            return;
        }
        if (_loading)
            return;

        var end = _history.NowMs;
        var start = end - (long)TimeSpan.FromDays(_days).TotalMilliseconds;
        var hosts = CheckedHosts();
        var selected = Characters.Where(c => c.IsChecked && c.Persona is not null).ToList();
        var series = selected
            .Select(c => new ScoreGraphSeries(c.Label,
                _history.Points.GetValueOrDefault(c.Persona!.Value, []),
                _history.PersonaSessions.GetValueOrDefault(c.Persona!.Value, []),
                _history.Permadeaths.GetValueOrDefault(c.Persona!.Value, [])))
            .ToList();
        var runs = _history.Runs.Where(r => hosts.Contains(r.Host)).Select(r => r.Span).ToList();
        var seriesOf = selected.Select((c, i) => (c.Persona!.Value, i)).ToDictionary(x => x.Value, x => x.i);
        var switches = _history.Switches(hosts, seriesOf.Keys.ToHashSet())
            .Select(w => new ScoreSwitch(seriesOf[w.Persona], w.Session, w.From.Persona))
            .ToList();

        // Rebuilding the rows re-templates them on the UI thread; only a change of window or of who is
        // selected changes what they say.
        var statsKey = (start, end, string.Join("\n", selected.Select(c => c.Host + "\t" + c.Label)));
        if (_statsKey != statsKey)
        {
            _statsKey = statsKey;
            Stats.Clear();
            for (var i = 0; i < series.Count; i++)
            {
                if (ScoreGraphPlot.Stats(ScoreGraphPlot.Window(series[i].Points, start, end)) is not { } s)
                    continue;
                var net = s.Gained - s.Lost;
                Stats.Add(new ScoreStatRow(series[i].Label, ScoreGraphView.SeriesColor(i),
                    N(s.Start), N(s.Low), N(s.High), "+" + N(s.Gained), "-" + N(s.Lost),
                    (net > 0 ? "+" : net < 0 ? "-" : "") + N(Math.Abs(net)),
                    Color.FromArgb(net > 0 ? "#56d364" : net < 0 ? "#ff7b72" : "#e6edf3")));
            }
        }

        Status = _loadStatus
            ?? (_history.Points.Count == 0 ? "No score recorded yet."
                : hosts.Count == 0 ? "No server selected."
                : series.Count == 0 ? "No character selected."
                : Stats.Count == 0 ? $"No score recorded in the last {DaysLabel}."
                : string.Empty);
        Scene = new ScoreGraphScene(start, end, _isSqueezed, _scale.BucketMs, series, runs, switches, _isZero);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Summary(IEnumerable<ScoreChoice> choices, string plural)
    {
        var on = choices.Where(c => c.IsChecked).Select(c => c.Label).ToList();
        return on.Count switch
        {
            0 => "none",
            1 or 2 => string.Join(", ", on),
            _ => $"{on.Count.ToString(CultureInfo.InvariantCulture)} {plural}",
        };
    }
}
