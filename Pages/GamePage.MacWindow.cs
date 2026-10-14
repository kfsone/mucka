#if MACCATALYST
using Mucka.Combat;
using Mucka.ViewModels;

namespace Mucka.Pages;

// The desktop window rules on a Mac: MAUI's own Window sizes, in dp, where the Windows build drives
// AppWindow in physical pixels. The width arithmetic is CombatRailResize's, shared with Windows.
public partial class GamePage
{
    /// <summary>Same member the Windows build exposes, which App.CreateWindow calls.</summary>
    internal static double PreferredWindowWidthDp(
        double charWidthDp, bool panelExpanded, double viewColumns = CombatRailResize.DefaultViewColumns)
        => CombatRailResize.PreferredWindowWidthDp(charWidthDp, panelExpanded, viewColumns);

    /// <summary>Called once from OnAppearing: sizes the window for the terminal plus the panel, then
    /// keeps its minimum and width in step with the panel and the column count.</summary>
    private void SetUpMacWindow()
    {
        // Hooked once per set-up and unhooked in TearDownMacWindow: the Window outlives every page, so
        // a handler left on it keeps the whole page (view model, terminal) reachable.
        if (_macWindowHooked) TearDownMacWindow();
        _macWindowHooked = true;
        _vm.SidePanel.PropertyChanged += OnMacWindowSidePanelChanged;
        _vm.PropertyChanged += OnMacWindowVmChanged;
        // The pane is laid out after the page's own size is allocated, and moves when the panel does.
        Terminal.SizeChanged += OnMacTerminalSizeChanged;
        if (Window is { } owner)
        {
            _macSizeWindow = owner;
            owner.SizeChanged += OnMacWindowSizeChanged;
        }
        // A fixed column count sizes the window to fit, as the Windows build does at launch; auto
        // keeps whatever width the player has the window at.
        FitMacWindow(snapToColumns: _vm.MaxColumns > 0);
        SetUpMacCombatPanel();
        SetUpMacScroll();
        SetUpMacSelect();
        SetUpMacInput();
    }

    private bool _macWindowHooked;
    private Window? _macSizeWindow;

    private void OnMacWindowSidePanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidePanelViewModel.IsPanelExpanded))
        {
            BeginMacResizeSettle();
            FitMacWindow();
        }
    }

    private void OnMacWindowVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.MaxColumns))
            FitMacWindow(snapToColumns: true);
    }

    private void OnMacTerminalSizeChanged(object? sender, EventArgs e) => RecalculateColumns(Width);

    // The window has reached what was asked for; from here its real width is the truth.
    private void OnMacWindowSizeChanged(object? sender, EventArgs e)
    {
        if (sender is Window owner && _macCommandedWidth is { } asked
            && Math.Abs(CurrentMacWidthActual(owner) - asked) < 1.5)
            _macCommandedWidth = null;
    }

    /// <summary>Detaches everything SetUpMacWindow hung on objects that outlive the page: the app's
    /// Window and the view models.</summary>
    private void TearDownMacWindow()
    {
        _macWindowHooked = false;
        _macSettleToken++;
        _macResizeSettling = false;
        if (_macSizeWindow is { } w) w.SizeChanged -= OnMacWindowSizeChanged;
        _macSizeWindow = null;
        _vm.SidePanel.PropertyChanged -= OnMacWindowSidePanelChanged;
        _vm.PropertyChanged -= OnMacWindowVmChanged;
        Terminal.SizeChanged -= OnMacTerminalSizeChanged;
        _vm.SidePanel.CombatStatsChanged -= OnMacCombatStatsChanged;
        _vm.SidePanel.PropertyChanged -= OnMacCombatVisibilityChanged;
    }

    /// <summary>The window's width as it really is. MAUI's <c>Window.Width</c> can hold the last width
    /// that was asked for rather than the one the player has dragged the window to.</summary>
    private double CurrentMacWidth(Window window)
    {
        // A width this page has asked for and the window has not yet reached is what the window is
        // about to be, and the next calculation must start from it: MAUI applies a requested size a
        // moment later, so reading the real width straight after asking for another one sees the old.
        if (_macCommandedWidth is { } asked && DateTime.UtcNow - _macCommandedAtUtc < TimeSpan.FromSeconds(1.5))
            return asked;
        return window.Handler?.PlatformView is UIKit.UIWindow native && native.Bounds.Width > 0
            ? native.Bounds.Width
            : window.Width;
    }

    private static double CurrentMacWidthActual(Window window)
        => window.Handler?.PlatformView is UIKit.UIWindow native && native.Bounds.Width > 0
            ? native.Bounds.Width
            : window.Width;

    private double? _macCommandedWidth;
    private DateTime _macCommandedAtUtc;

    private void SetMacWidth(Window window, double width)
    {
        _macCommandedWidth = width;
        _macCommandedAtUtc = DateTime.UtcNow;
        window.Width = width;
    }

    /// <summary>The widest the window may be asked to become: the screen it is on.</summary>
    private static double MacScreenWidth => UIKit.UIScreen.MainScreen.Bounds.Width;

    /// <summary>Sets the minimum width to what the columns and the panel need, and widens the window
    /// when it is narrower than that. <paramref name="snapToColumns"/> also takes it to the width
    /// that fits the columns plus the same two breathing columns the Windows build uses.</summary>
    private void FitMacWindow(bool snapToColumns = false)
    {
        if (Window is not { } window) return;
        var panel = _vm.SidePanel.IsPanelExpanded;
        var floor = PreferredWindowWidthDp(CharWidthDp, panel, _vm.MaxColumns);
        ApplyMacMinimum(window);

        var rail = _macRailApplied ? RailPanelWidthDp : 0.0;
        var target = floor;
        if (snapToColumns)
        {
            target = (_vm.MaxColumns > 0
                ? PreferredWindowWidthDp(CharWidthDp, panel, _vm.MaxColumns + 2.0)
                : PreferredWindowWidthDp(CharWidthDp, panel)) + rail;
        }
        target = Math.Min(Math.Max(target, floor + rail), MacScreenWidth);
        if (snapToColumns || CurrentMacWidth(window) < floor + rail)
            SetMacWidth(window, target);
    }

    /// <summary>The narrowest the window may be: the terminal's columns and the panel, plus the rail
    /// while it is applied. Lowered BEFORE a hide shrinks the window - the system holds the window at
    /// its minimum, so shrinking first leaves it stuck at the old, wider one.</summary>
    private void ApplyMacMinimum(Window window)
        => window.MinimumWidth = PreferredWindowWidthDp(
               CharWidthDp, _vm.SidePanel.IsPanelExpanded, _vm.MaxColumns)
           + (_macRailApplied ? RailPanelWidthDp : 0.0);

    // While the window and the layout disagree about the width, the column count is not measured:
    // the pane narrows the moment the rail is shown and the window catches up a moment later, and
    // measuring in between told the server 86 columns and then 90 again.
    private bool _macResizeSettling;
    private int _macSettleToken;

    /// <summary>True while <see cref="RecalculateColumns"/> should leave the column count alone.</summary>
    private bool MacResizeSettling => _macResizeSettling;

    private void BeginMacResizeSettle()
    {
        _macResizeSettling = true;
        var token = ++_macSettleToken;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(700), () =>
        {
            if (token != _macSettleToken) return;
            _macResizeSettling = false;
            RecalculateColumns(Width);
        });
    }

    // -- The Combat Rail ---------------------------------------------------------
    // What the rail costs the window at the current Stats setting.
    private double RailPanelWidthDp => CombatRailResize.PanelWidthDp(_vm.SidePanel.IsCombatStatsEnabled);

    private bool _macRailApplied;
    private double _macRailDeltaDp;

    /// <summary>The rail's animation layers (glow, trim, tick fill, flee pill) are set up by
    /// SetUpMacCombatLayers, which puts each at its rest state: at their XAML defaults they paint over
    /// the panel - the tick fill is a white box.</summary>
    private void SetUpMacCombatPanel()
    {

        ApplyMacCombatPanelWidth();
        SetUpMacCombatLayers();
        _vm.SidePanel.CombatStatsChanged += OnMacCombatStatsChanged;
        _vm.SidePanel.PropertyChanged += OnMacCombatVisibilityChanged;
        // A persisted "rail shown" is already true here and raises no change event.
        if (_vm.SidePanel.IsCombatPanelVisible)
            ResizeMacWindowForCombatPanel(showing: true);
    }

    private void OnMacCombatStatsChanged(bool showStats)
    {
        ApplyMacCombatPanelWidth();
        if (_vm.SidePanel.IsCombatPanelVisible)
        {
            BeginMacResizeSettle();
            ResizeMacWindowForCombatPanel(showing: false);
            ResizeMacWindowForCombatPanel(showing: true);
        }
        _ = _vm.PersistCombatStatsAsync();
    }

    private void OnMacCombatVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SidePanelViewModel.IsCombatPanelVisible)) return;
        ResizeMacWindowForCombatPanel(_vm.SidePanel.IsCombatPanelVisible);
        _ = _vm.PersistCombatRailVisibilityAsync();
    }

    private void ApplyMacCombatPanelWidth()
    {
        CombatPanelCanvas.ShowStats = RailShowStats;
        CombatPanelBorder.WidthRequest = RailPanelWidthDp;
        // Everything positioned over the canvas follows its width.
        if (_macSweep is not null || _macFlee is not null)
            ReseatMacCombatLayers();
    }

    /// <summary>Grows or shrinks the window by the rail's width. The arithmetic is
    /// <see cref="CombatRailResize.ComputeToggle"/>'s; dp stand in for pixels (dpi 96).</summary>
    private void ResizeMacWindowForCombatPanel(bool showing)
    {
        if (Window is not { } window || showing == _macRailApplied) return;
        BeginMacResizeSettle();
        // In auto columns the count in use stands in for the setting: the server takes at most 160, so
        // a window wider than that has room to spare, and the rail should sit in it.
        var columns = _vm.MaxColumns > 0 ? _vm.MaxColumns : _vm.EffCols;
        var current = CurrentMacWidth(window);
        var result = CombatRailResize.ComputeToggle(
            showing, (int)Math.Round(current), 96, columns, CharWidthDp,
            _vm.SidePanel.IsPanelExpanded, _macRailDeltaDp, RailPanelWidthDp);
        _macRailDeltaDp = result.NewAppliedDeltaDp;
        _macRailApplied = showing;
        // Never past the screen.
        var target = Math.Min(result.TargetWidthPx, MacScreenWidth);
        if (!showing)
            ApplyMacMinimum(window);   // before the shrink, which the old minimum would hold off
        if (Math.Abs(target - current) >= 1.0)
            SetMacWidth(window, target);
        if (showing)
            ApplyMacMinimum(window);
        RefocusAfterResize();
    }
}
#endif
