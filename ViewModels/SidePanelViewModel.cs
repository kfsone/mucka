using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class SidePanelViewModel : BaseViewModel, IDisposable
{
    private bool _isPanelExpanded;
    private int _activeTab;
    private string _characterName = "";

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set => SetAndNotify(ref _isPanelExpanded, value,
            [nameof(IsPanelCollapsed), nameof(PanelToggleGlyph)]);
    }
    public bool IsPanelCollapsed => !_isPanelExpanded;
    // ▼ when collapsed (click to show panel), ▶ when expanded (click to hide panel)
    public string PanelToggleGlyph => _isPanelExpanded ? "▶" : "▼";

    public int ActiveTab
    {
        get => _activeTab;
        set => SetAndNotify(ref _activeTab, value,
            [nameof(IsExtrasTab), nameof(IsAboutTab)]);
    }
    public bool IsExtrasTab => _activeTab == 0;
    public bool IsAboutTab  => _activeTab == 1;

    public string CharacterName
    {
        get => _characterName;
        set => SetAndNotify(ref _characterName, value, [nameof(HasCharacterName)]);
    }
    public bool HasCharacterName => !string.IsNullOrEmpty(_characterName);

    public string AppVersion => AppInfo.VersionString;

    public ObservableCollection<string> WhosList { get; } = new();

    // Accumulates names on the read-loop thread; swapped into WhosList atomically on FewListComplete.
    private readonly List<string> _pendingWhos = new();

    public ICommand TogglePanelCommand { get; }
    public ICommand SetTabCommand { get; }
    public ICommand OpenLinkCommand { get; }

    public SidePanelViewModel()
    {
        TogglePanelCommand = new Command(() => IsPanelExpanded = !IsPanelExpanded);
        SetTabCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var tab))
                ActiveTab = tab;
        });
        OpenLinkCommand = new Command<string>(url =>
        {
            if (!string.IsNullOrWhiteSpace(url))
                _ = Launcher.OpenAsync(new Uri(url));
        });
    }

    /// <summary>
    /// Called when the parser opens a FEW-response context (C12+C08+C05).
    /// Clears the accumulation buffer; WhosList is not touched until the response is complete.
    /// Fires on the TCP read thread — no marshal needed (_pendingWhos is read-loop-only).
    /// </summary>
    public void OnFewListStarting()
        => _pendingWhos.Clear();

    /// <summary>
    /// Called from the TCP read thread for each player name in the FEW response.
    /// Accumulates into the pending buffer; WhosList is not updated yet.
    /// </summary>
    public void OnFewPlayerReceived(string playerName)
        => _pendingWhos.Add(playerName);

    /// <summary>
    /// Called when the FEW-response context closes — all names have been delivered.
    /// Snapshots the pending buffer on the read-loop thread, then marshals a single
    /// atomic WhosList replacement to the UI thread.
    /// </summary>
    public void OnFewListComplete()
    {
        var snapshot = _pendingWhos.ToList();
        _pendingWhos.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            WhosList.Clear();
            foreach (var name in snapshot)
                WhosList.Add(name);
        });
    }

    public void Dispose() { }
}
