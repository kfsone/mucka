using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class SidePanelViewModel : BaseViewModel, IDisposable
{
    private bool _isPanelExpanded;
    private int _activeTab;

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set => SetAndNotify(ref _isPanelExpanded, value,
            [nameof(IsPanelCollapsed), nameof(PanelToggleGlyph)]);
    }
    public bool IsPanelCollapsed => !_isPanelExpanded;
    // ◀ when collapsed (click to expand), ▶ when expanded (click to collapse)
    public string PanelToggleGlyph => _isPanelExpanded ? "▶︎" : "◄︎";

    public int ActiveTab
    {
        get => _activeTab;
        set => SetAndNotify(ref _activeTab, value,
            [nameof(IsWhoTab), nameof(IsKillsTab), nameof(IsFightTab), nameof(IsPtsTab)]);
    }
    public bool IsWhoTab   => _activeTab == 0;
    public bool IsKillsTab => _activeTab == 1;
    public bool IsFightTab => _activeTab == 2;
    public bool IsPtsTab   => _activeTab == 3;

    public ObservableCollection<string> WhosList  { get; } = new();
    public ObservableCollection<string> KillsList { get; } = new();

    public ICommand TogglePanelCommand { get; }
    public ICommand SetTabCommand { get; }

    public SidePanelViewModel()
    {
        TogglePanelCommand = new Command(() => IsPanelExpanded = !IsPanelExpanded);
        SetTabCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var tab))
                ActiveTab = tab;
        });
    }

    /// <summary>
    /// Called when the parser opens a FEW-response context (C12+C08+C05).
    /// Clears the who list so it can be rebuilt from the incoming FewPlayerReady events.
    /// Fires on the TCP read thread — marshals to the UI thread.
    /// </summary>
    public void OnFewListStarting()
        => MainThread.BeginInvokeOnMainThread(() => WhosList.Clear());

    /// <summary>
    /// Called from the TCP read thread when the protocol decoder extracts a player name
    /// from the WHO-list color annotation in a FEW response.
    /// </summary>
    public void OnFewPlayerReceived(string playerName)
        => MainThread.BeginInvokeOnMainThread(() => WhosList.Add(playerName));

    public void Dispose() { }
}
