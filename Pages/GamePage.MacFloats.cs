#if MACCATALYST
using Mucka.Combat;
using Mucka.Rendering;
using UIKit;

namespace Mucka.Pages;

// The Combat Rail's damage floats and unseen-tile marquees on a Mac, wired the way the Windows
// handlers in GamePage.xaml.cs's Windows region wire them: the same budget, placement and emphasis
// rules from Mucka.Combat, with Core Animation layers (Rendering/MacRailEffects.cs) in place of
// Composition ones.
public partial class GamePage
{
    private const double MacFloatWidthDp = 92.0;
    private static readonly TimeSpan MacFloatDuration = RailFloatBudget.Lifetime;

    private readonly Label[] _macFloatLabels = new Label[RailFloatBudget.MaxInFlight];
    private readonly UILabel?[] _macFloatText = new UILabel?[RailFloatBudget.MaxInFlight];
    private readonly MacRailFloat?[] _macFloats = new MacRailFloat?[RailFloatBudget.MaxInFlight];
    private readonly RailFloatBudget _macFloatBudget = new();
    private (int Rows, int Live) _macFloatRosterShape = (-1, -1);
    private bool _macFloatsBuilt;

    private readonly ContentView[] _macMarqueeHosts = new ContentView[2];
    private readonly MacUnseenMarquee?[] _macMarquees = new MacUnseenMarquee?[2];
    private bool _macMarqueesBuilt;

    private double _macRailWidthDp;
    private double _macRailHeightDp;

    private static readonly UIColor MacFloatOutgoing = UIColor.FromRGB(0xF2, 0xF2, 0xF2);
    private static readonly UIColor MacFloatIncoming = UIColor.FromRGB(
        StaminaGlow.EdgeRed.R, StaminaGlow.EdgeRed.G, StaminaGlow.EdgeRed.B);
    private static readonly UIColor MacFloatMiss = UIColor.FromRGB(0x76, 0x76, 0x76);
    private static readonly UIColor MacFloatGain = UIColor.FromRGB(0x16, 0xC6, 0x0C);

    private void SetUpMacRailEffects()
    {
        SetUpMacMarquees();
        SetUpMacFloats();
        _vm.SidePanel.PropertyChanged += OnMacRailEffectsPropertyChanged;
    }

    private void OnMacRailEffectsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModels.SidePanelViewModel.Live):
                SyncMacFloatRoster();
                SyncMacMarquees();
                break;
            case nameof(ViewModels.SidePanelViewModel.IsCombatFloatsEnabled):
                // Switching them off takes the ones already in the air with it.
                if (!_vm.SidePanel.IsCombatFloatsEnabled)
                    CancelMacFloats();
                break;
            case nameof(ViewModels.SidePanelViewModel.IsCombatPanelVisible):
                // Floats in flight were placed against geometry that no longer applies.
                CancelMacFloats();
                SyncMacMarquees();
                break;
        }
    }

    // -- Damage floats ------------------------------------------------------------------------

    private void SetUpMacFloats()
    {
        for (var i = 0; i < _macFloatLabels.Length && !_macFloatsBuilt; i++)
        {
            var label = new Label
            {
                Text = string.Empty,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                WidthRequest = MacFloatWidthDp,
                HeightRequest = RailFloatPlacement.CombatFloatHeightDp,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                LineBreakMode = LineBreakMode.NoWrap,
                InputTransparent = true,
            };
            _macFloatLabels[i] = label;
            var slot = i;
            label.HandlerChanged += (_, _) => OnMacFloatHandlerChanged(slot);
            CombatPanelLayers.Children.Add(label);
        }
        _macFloatsBuilt = true;

        for (var i = 0; i < _macFloatLabels.Length; i++)
            OnMacFloatHandlerChanged(i);

        CombatPanelCanvas.SizeChanged += OnMacRailSizeChanged;
        OnMacRailSizeChanged(CombatPanelCanvas, EventArgs.Empty);
        _vm.SidePanel.CombatFloatRaised += OnMacFloatRaised;
    }

    private void TearDownMacFloats()
    {
        _vm.SidePanel.CombatFloatRaised -= OnMacFloatRaised;
        CombatPanelCanvas.SizeChanged -= OnMacRailSizeChanged;
        _macFloatBudget.Clear();
        for (var i = 0; i < _macFloats.Length; i++)
        {
            _macFloats[i]?.Rest();
            _macFloats[i] = null;
            _macFloatText[i] = null;
        }
        for (var i = 0; i < _macMarquees.Length; i++)
        {
            _macMarquees[i]?.Stop();
            _macMarquees[i] = null;
        }
    }

    private void OnMacFloatHandlerChanged(int slot)
    {
        _macFloats[slot]?.Release();
        _macFloats[slot] = null;
        _macFloatText[slot] = null;

        if (_macFloatLabels[slot].Handler?.PlatformView is not UILabel text)
            return;
        // Decoration only: nothing here takes a click or the keyboard.
        text.UserInteractionEnabled = false;
        text.TextAlignment = UITextAlignment.Left;
        text.TextColor = MacFloatMiss;
        _macFloatText[slot] = text;
        _macFloats[slot] = MacRailFloat.Attach(MacAnimatedView.Of(_macFloatLabels[slot].Handler) ?? text);
    }

    private void OnMacRailSizeChanged(object? sender, EventArgs e)
    {
        var width = CombatPanelCanvas.Width;
        var height = CombatPanelCanvas.Height;
        if (width == _macRailWidthDp && height == _macRailHeightDp)
            return;
        _macRailWidthDp = width;
        _macRailHeightDp = height;
        // Placement was worked out against the old geometry.
        CancelMacFloats();
        SyncMacMarquees();
    }

    private void CancelMacFloats()
    {
        _macFloatBudget.Clear();
        _macFloatRosterShape = (-1, -1);
        foreach (var layer in _macFloats)
            layer?.Rest();
    }

    private void SyncMacFloatRoster()
    {
        var roster = _vm.SidePanel.Live.Roster;
        var shape = (roster.Rows.Count, roster.LiveCount);
        if (shape == _macFloatRosterShape)
            return;
        _macFloatRosterShape = shape;
        // The roster's shape changed, so floats anchored to an opponent's row no longer sit on it.
        var freed = _macFloatBudget.ClearOpponentAnchored();
        for (var i = 0; freed != 0 && i < _macFloats.Length; i++)
            if ((freed & (1 << i)) != 0)
                _macFloats[i]?.Rest();
    }

    private void OnMacFloatRaised(RailFloat floatEvent)
    {
        if (!_vm.SidePanel.IsCombatPanelVisible || !_vm.SidePanel.IsCombatFloatsEnabled)
            return;
        if (_macRailWidthDp <= 0 || _macRailHeightDp <= 0)
            return;
        if (CombatPanelCanvas.FloatOriginDp(
                _macRailWidthDp, _macRailHeightDp, floatEvent.RosterIndex, floatEvent.LiveCount) is not RailRect origin)
            return;
        if (_macFloatBudget.Admit(floatEvent.Kind, floatEvent.RosterIndex, floatEvent.AtUtc) is not RailFloatGrant grant)
            return;   // over budget; see RailFloatBudget for the shed policy

        var layer = _macFloats[grant.PoolSlot];
        if (layer is null || _macFloatText[grant.PoolSlot] is not { } text)
        {
            _macFloatBudget.Retire(grant.PoolSlot, grant.Token);
            return;
        }

        if (!string.Equals(text.Text, floatEvent.Text, StringComparison.Ordinal))
            text.Text = floatEvent.Text;
        text.TextColor = floatEvent.Kind switch
        {
            RailFloatKind.OutgoingHit => MacFloatOutgoing,
            RailFloatKind.IncomingHit => MacFloatIncoming,
            RailFloatKind.StaminaGain => MacFloatGain,
            _ => MacFloatMiss,
        };
        var size = (nfloat)RailFloatEmphasis.FontSizeFor(floatEvent.Magnitude);
        var font = UIFont.SystemFontOfSize(
            size, RailFloatEmphasis.IsHeavy(floatEvent.Magnitude) ? UIFontWeight.Black : UIFontWeight.Bold);
        if (font is not null)
            text.Font = font;

        var (x, y) = RailFloatPlacement.For(grant.Cluster, origin);
        layer.Play(x, y, MacFloatDuration, () =>
        {
            _macFloatBudget.Retire(grant.PoolSlot, grant.Token);
            text.Text = string.Empty;
        });
    }

    // -- Unseen-opponent marquees ---------------------------------------------------------------

    private void SetUpMacMarquees()
    {
        for (var i = 0; i < _macMarqueeHosts.Length && !_macMarqueesBuilt; i++)
        {
            var host = new ContentView
            {
                WidthRequest = 1,
                HeightRequest = 1,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                InputTransparent = true,
            };
            _macMarqueeHosts[i] = host;
            var slot = i;
            host.HandlerChanged += (_, _) => OnMacMarqueeHandlerChanged(slot);
            CombatPanelLayers.Children.Add(host);
        }
        _macMarqueesBuilt = true;
        for (var i = 0; i < _macMarqueeHosts.Length; i++)
            OnMacMarqueeHandlerChanged(i);
    }

    private void OnMacMarqueeHandlerChanged(int slot)
    {
        _macMarquees[slot]?.Stop();
        _macMarquees[slot] = null;
        if (_macMarqueeHosts[slot].Handler?.PlatformView is not UIView view)
            return;
        view.UserInteractionEnabled = false;
        _macMarquees[slot] = MacUnseenMarquee.Attach(view);
        SyncMacMarquees();
    }

    private void SyncMacMarquees()
    {
        var panel = _vm.SidePanel;
        var live = panel.Live;
        var rows = live.Roster.Rows;
        var used = 0;
        if (panel.IsCombatPanelVisible && live.HasEncounter && _macRailWidthDp > 0 && _macRailHeightDp > 0)
        {
            for (var i = 0; i < rows.Count && used < _macMarquees.Length; i++)
            {
                if (!rows[i].IsUnseen || !rows[i].IsLive)
                    continue;
                if (CombatPanelCanvas.RowRectDp(_macRailWidthDp, _macRailHeightDp, rows, i, live.Roster.LiveCount)
                    is not RailRect rect)
                    continue;
                _macMarquees[used]?.Show(rect);
                used++;
            }
        }
        for (var i = used; i < _macMarquees.Length; i++)
            _macMarquees[i]?.Hide();
    }
}
#endif
