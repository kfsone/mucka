#if MACCATALYST
using Mucka.Combat;
using Mucka.Rendering;
using Mucka.ViewModels;
using UIKit;

namespace Mucka.Pages;

// The Combat Rail's animated layers on a Mac, driven the way the Windows build drives them (the
// handlers in GamePage.xaml.cs's Windows region): the same view-model events, the same geometry from
// CombatRailView, the Core Animation layers in Rendering/MacCombatLayers.cs.
public partial class GamePage
{
    private MacPulseLayer? _macGlow;
    private MacPulseLayer? _macTrim;
    private MacTickSweep? _macSweep;
    private MacFleePulse? _macFlee;
    private bool _macSweepRunning;
    private DateTime? _macSweepAnchorUtc;
    private MudSharp.Combat.FleePillStatus? _macPillStatus;
    private readonly Mucka.Audio.CombatMetronome _macMetronome = new();
    private bool _macMetronomeRunning;
    private DateTime? _macMetronomeAnchorUtc;
    private Color? _macGlowColor;
    private Color? _macEdgeColor;

    private static readonly Color MacEdgeLive = Color.FromRgb(
        StaminaGlow.Amber.R, StaminaGlow.Amber.G, StaminaGlow.Amber.B);
    private static readonly Color MacFleePillStroke = Color.FromArgb("#FF0000");
    private static readonly Color MacFleePillCriticalFill = Color.FromArgb("#33FF0000");

    /// <summary>The rail's live content width - narrower with the stat rows off.</summary>
    private double RailContentWidthDp => CombatRailResize.ContentWidthDp(_vm.SidePanel.IsCombatStatsEnabled);

    private void SetUpMacCombatLayers()
    {
        CombatPanelGlow.HandlerChanged += (_, _) => AttachMacGlow();
        CombatPanelTrim.HandlerChanged += (_, _) => AttachMacTrim();
        CombatTickSweep.HandlerChanged += (_, _) => AttachMacTickSweep();
        CombatFleePill.HandlerChanged += (_, _) => AttachMacFleePill();
        CombatFleePillHit.HandlerChanged += (_, _) => AttachMacFleePillHit();
        AttachMacGlow();
        AttachMacTrim();
        AttachMacTickSweep();
        AttachMacFleePill();
        AttachMacFleePillHit();
        AttachMacMetronomeHit();

        _vm.SidePanel.PropertyChanged += OnMacCombatPropertyChanged;
        UpdateMacCombatEdges();
        SetUpMacRailEffects();
    }

    /// <summary>Called when the player leaves the game for good. Not tied to the page's Unloaded event:
    /// a modal raises that too.</summary>
    private void TearDownMacCombat()
    {
        StopMacCombatLayers();
        _macMetronome.Dispose();
        TearDownMacFloats();
        // The profile page is not held to the game's width.
        if (Window is { } window)
            window.MinimumWidth = 0;
    }

    /// <summary>Puts every running animation back: they can be dropped while the window is hidden.</summary>
    private void ReassertMacCombatLayers()
    {
        _macGlow?.Reassert();
        _macTrim?.Reassert();
        _macFlee?.Reassert();
        foreach (var marquee in _macMarquees)
            marquee?.Reassert();
        if (_macSweepRunning && _macSweepAnchorUtc is { } anchor)
            _macSweep?.Restart(anchor);
    }

    private void StopMacCombatLayers()
    {
        _macGlow?.Stop();
        _macTrim?.Stop();
        _macSweep?.Stop();
        _macFlee?.Stop();
        _macSweepRunning = false;
        _macMetronome.Stop();
        _macMetronomeRunning = false;
    }

    /// <summary>Re-seats everything positioned from the rail's width: the stat rows changed it.</summary>
    private void ReseatMacCombatLayers()
    {
        AttachMacTickSweep();
        AttachMacFleePill();
        AttachMacFleePillHit();
        AttachMacMetronomeHit();
    }

    private void OnMacCombatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SidePanelViewModel.GlowLevel):
                ApplyMacPanelGlow();
                UpdateMacCombatEdges();
                break;
            case nameof(SidePanelViewModel.Live):
            case nameof(SidePanelViewModel.IsCombatPanelVisible):
            case nameof(SidePanelViewModel.IsCombatGracePeriod):
            case nameof(SidePanelViewModel.TickPhaseUtc):
                UpdateMacTickSweep();
                UpdateMacCombatEdges();
                UpdateMacFleePill();
                UpdateMacMetronome();
                break;
            case nameof(SidePanelViewModel.IsCombatMetronomeEnabled):
                // Arming mid-fight joins the fight already in progress, on the beat from its anchor.
                UpdateMacMetronome();
                break;
        }
    }

    // -- Glow and trim ------------------------------------------------------------------------

    private void AttachMacGlow()
    {
        _macGlow?.Release();
        _macGlow = null;
        if (MacAnimatedView.Of(CombatPanelGlow.Handler) is { } view)
        {
            _macGlow = MacPulseLayer.Attach(view);
            ApplyMacPanelGlow();   // a level already published must reach a fresh layer
        }
    }

    private void AttachMacTrim()
    {
        _macTrim?.Release();
        _macTrim = null;
        if (MacAnimatedView.Of(CombatPanelTrim.Handler) is { } view)
        {
            _macTrim = MacPulseLayer.Attach(view);
            ApplyMacPanelGlow();
        }
    }

    private void ApplyMacPanelGlow()
    {
        var level = _vm.SidePanel.GlowLevel;
        var look = StaminaGlow.LookFor(level);
        if (look is { } l)
        {
            var color = Color.FromRgb(l.R, l.G, l.B);
            if (_macGlowColor != color)
            {
                _macGlowColor = color;
                CombatPanelGlow.Color = color;
                CombatPanelTrim.Stroke = new SolidColorBrush(color);
            }
        }
        _macTrim?.SetPulse(look is { } t ? new MacPulseLayer.Pulse(t.TrimPeak, t.TrimTrough, t.PeriodMilliseconds) : null);
        _macGlow?.SetPulse(look is { } g && level >= StaminaGlow.WholePanelFromLevel
            ? new MacPulseLayer.Pulse(g.Peak, g.Trough, g.PeriodMilliseconds)
            : null);
    }

    // -- Combat edges -------------------------------------------------------------------------

    private void UpdateMacCombatEdges()
    {
        var panel = _vm.SidePanel;
        var live = panel.Live.InCombat && !panel.IsCombatGracePeriod;
        var color = StaminaGlow.EdgeColorFor(panel.GlowLevel) is var (r, g, b)
            ? Color.FromRgb(r, g, b)
            : live ? MacEdgeLive : Colors.Transparent;
        if (_macEdgeColor == color)
            return;
        _macEdgeColor = color;
        var brush = new SolidColorBrush(color);
        CombatEdgeTerminal.Stroke = brush;
        CombatEdgeInput.Stroke = brush;
    }

    // -- Tick meter ---------------------------------------------------------------------------

    private void AttachMacTickSweep()
    {
        _macSweep?.Release();
        _macSweep = null;

        var (left, right, bottom, height) = CombatRailView.TickTrackDp(RailContentWidthDp, RailShowStats);
        CombatTickSweep.Margin = new Thickness(left, 0, right, bottom);
        CombatTickSweep.HeightRequest = height;

        if (MacAnimatedView.Of(CombatTickSweep.Handler) is { } view)
        {
            _macSweep = MacTickSweep.Attach(view);
            _macSweepRunning = false;
            UpdateMacTickSweep();   // the panel can be opened mid-fight
            // The bar's geometry is read when it starts, and this element has no size until it has been
            // laid out: start again once it has one.
            CombatTickSweep.SizeChanged -= OnMacTickSweepResized;
            CombatTickSweep.SizeChanged += OnMacTickSweepResized;
        }
    }

    private void OnMacTickSweepResized(object? sender, EventArgs e)
    {
        if (_macSweepRunning && _macSweepAnchorUtc is { } anchor)
            _macSweep?.Restart(anchor);
    }

    private void UpdateMacTickSweep()
    {
        if (_macSweep is null)
            return;
        var panel = _vm.SidePanel;
        var anchor = panel.TickPhaseUtc;
        var meaningful = panel.IsCombatPanelVisible && panel.Live.InCombat && !panel.IsCombatGracePeriod;
        var shouldSweep = anchor is not null && meaningful;
        // A change of anchor restarts a running sweep: the first swing is usually when the phase is
        // learned to have been wrong.
        if (shouldSweep == _macSweepRunning && !(shouldSweep && anchor != _macSweepAnchorUtc))
            return;
        _macSweepRunning = shouldSweep;
        _macSweepAnchorUtc = anchor;
        if (shouldSweep)
            _macSweep.Restart(anchor!.Value);
        else
            _macSweep.Stop();
    }

    // -- Tick metronome -----------------------------------------------------------------------

    private bool MacTickIsMeaningful()
    {
        var panel = _vm.SidePanel;
        return panel.IsCombatPanelVisible && panel.Live.InCombat && !panel.IsCombatGracePeriod;
    }

    private void UpdateMacMetronome()
    {
        // No anchor means the phase is unknown, and silence is the honest option. The click also waits
        // for the estimate to have settled, which the bar does not: a briefly wrong timer that visibly
        // corrects itself is honest in a way a confidently wrong sound is not.
        var anchor = _vm.SidePanel.TickPhaseUtc;
        var shouldRun = _vm.SidePanel.IsCombatMetronomeEnabled && anchor is not null
            && _vm.SidePanel.IsTickPhaseSettled && MacTickIsMeaningful();

        _macMetronome.SetEnabled(_vm.SidePanel.IsCombatMetronomeEnabled);
        if (shouldRun == _macMetronomeRunning && anchor == _macMetronomeAnchorUtc)
            return;

        _macMetronomeAnchorUtc = anchor;
        // Stop first even when starting: Start is idempotent, so a running metronome would otherwise
        // ignore a better anchor arriving. The cache holds what ARMED, not what was wanted.
        _macMetronome.Stop();
        _macMetronomeRunning = shouldRun && _macMetronome.Start(anchor!.Value, MacTickIsMeaningful);
    }

    private void AttachMacMetronomeHit()
    {
        // Positioned from the track the canvas draws the switch against, so it cannot drift off it.
        var (_, _, bottom, _) = CombatRailView.TickTrackDp(RailContentWidthDp, RailShowStats);
        const double sizeDp = 24.0;
        const double rightInsetDp = 6.0;
        const double verticalNudgeDp = 2.0;
        CombatMetronomeHit.WidthRequest = sizeDp;
        CombatMetronomeHit.HeightRequest = sizeDp;
        CombatMetronomeHit.Margin = new Thickness(0, 0, rightInsetDp, bottom - (sizeDp / 2.0) + verticalNudgeDp);
        CombatMetronomeHit.IsVisible = true;

        // The metronome is on by default and a default raises no change, so adopt it here.
        UpdateMacMetronome();
    }

    // -- Flee pill ----------------------------------------------------------------------------

    private void AttachMacFleePill()
    {
        _macFlee?.Release();
        _macFlee = null;
        _macPillStatus = null;

        var (left, right, bottom, height, radius) = CombatRailView.FleePillDp(RailContentWidthDp, RailShowStats);
        CombatFleePill.Margin = new Thickness(left, 0, right, bottom);
        CombatFleePill.HeightRequest = height;
        CombatFleePill.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
        {
            CornerRadius = new CornerRadius(radius),
        };

        if (CombatFleePill.Handler?.PlatformView is UIView pill)
        {
            // Decoration only: CombatFleePillHit owns every click in this rectangle.
            pill.UserInteractionEnabled = false;
            _macFlee = MacFleePulse.Attach(MacAnimatedView.Of(CombatFleePill.Handler) ?? pill);
            UpdateMacFleePill();
        }
    }

    private void UpdateMacFleePill()
    {
        var panel = _vm.SidePanel;
        var status = panel.IsCombatGracePeriod ? MudSharp.Combat.FleePillStatus.Hidden : panel.Live.FleePill;
        if (_macPillStatus == status)
            return;
        _macPillStatus = status;

        // The hit target exists only while the pill does: a live button over an invisible pill would
        // turn a stray click into a flee, which costs a share of total score.
        var shown = status != MudSharp.Combat.FleePillStatus.Hidden;
        if (CombatFleePillHit.IsVisible != shown)
            CombatFleePillHit.IsVisible = shown;

        switch (status)
        {
            case MudSharp.Combat.FleePillStatus.Caution:
                CombatFleePill.Stroke = new SolidColorBrush(MacFleePillStroke);
                CombatFleePill.BackgroundColor = Colors.Transparent;
                _macFlee?.Pulse(0.40);
                break;
            case MudSharp.Combat.FleePillStatus.EscapeNow:
                CombatFleePill.Stroke = new SolidColorBrush(MacFleePillStroke);
                CombatFleePill.BackgroundColor = MacFleePillCriticalFill;
                _macFlee?.Pulse(0.55);
                break;
            default:
                _macFlee?.Stop();
                break;
        }
    }

    private void AttachMacFleePillHit()
    {
        var (left, right, bottom, height, _) = CombatRailView.FleePillDp(RailContentWidthDp, RailShowStats);
        // Width stated explicitly: a Fill button would make a flee reachable from anywhere along the row.
        CombatFleePillHit.Margin = new Thickness(left, 0, 0, bottom);
        CombatFleePillHit.WidthRequest = RailContentWidthDp - left - right;
        CombatFleePillHit.HeightRequest = height;
        CombatFleePillHit.Clicked -= OnMacFleePillClicked;
        CombatFleePillHit.Clicked += OnMacFleePillClicked;
    }

    private void OnMacFleePillClicked(object? sender, EventArgs e)
    {
        if (Terminal.IsHistoryMode) Terminal.ScrollToBottom();
        _vm.Flee();
        FocusInput();
    }
}
#endif
