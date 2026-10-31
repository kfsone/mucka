namespace Mucka.Pages;

// Two fits for the stats bar, each measured against the room it has:
// - Android and iOS: the one-row bar is chosen by column count, and a phone in landscape has the
//   columns without the width. On an iPhone 11 at 97 columns the effect icons were drawn over the
//   rec chip and the weather. The one-row bar sheds its maximums of 100 and over and the weather's
//   word when it does not fit, and gives way to the two-row bar when it still does not; the widths it
//   wanted are kept, so each comes back only when there is room for it.
// - Everywhere: the two-row bar carries the effect icons beside the score when they fit, and they
//   float below the bar when they do not. Observed on an iPad mini in portrait at 86 columns: the
//   one-row bar did not fit, and the two-row bar had room to spare between the score and (R).
public partial class GamePage
{
    // Room to spare before something comes back into the bar, so a fit that is just made does not flicker.
    private const double StatsBarSlackDp = 16;

    // BarEffects' left and right margins in GamePage.xaml.
    private const double BarEffectsMarginDp = 10;

#if MOBILE
    // What the one-row bar last wanted with everything on it, and with its maximums shed.
    private double _fullStatsWidthDp;
    private double _tightStatsWidthDp;
#endif
    private bool _statsFitQueued;

    private void SetUpStatsFit()
    {
        StatusBar.SizeChanged += OnStatsBarChanged;
        FullStatsBar.SizeChanged += OnStatsBarChanged;
        CompactStatsBar.SizeChanged += OnStatsBarChanged;
        FullStatsBar.MeasureInvalidated += OnStatsBarChanged;
        CompactStatsBar.MeasureInvalidated += OnStatsBarChanged;
    }

    // One check per dispatcher tick however many changes arrive in it.
    private void OnStatsBarChanged(object? sender, EventArgs e)
    {
        if (_statsFitQueued) return;
        _statsFitQueued = true;
        Dispatcher.Dispatch(FitStatsBar);
    }

    private void FitStatsBar()
    {
        _statsFitQueued = false;
#if MOBILE
        // In steps: the one-row bar first sheds what it can (StatsBarTight), and only then gives way
        // to the two-row bar; it comes back the same way, each step when there is room for it.
        if (FullStatsBar.IsVisible && FullStatsBar.Width > 0)
        {
            var wanted = FullStatsBar.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
            if (_vm.StatsBarTight) _tightStatsWidthDp = wanted;
            else _fullStatsWidthDp = wanted;
            if (wanted > FullStatsBar.Width + 0.5)
            {
                // Shedding changes the bar's text, which brings it back here to be measured again.
                if (!_vm.StatsBarTight) _vm.StatsBarTight = true;
                else _vm.StatsBarCrowded = true;
            }
            else if (_vm.StatsBarTight && _fullStatsWidthDp + StatsBarSlackDp <= FullStatsBar.Width)
            {
                _vm.StatsBarTight = false;
            }
        }
        else if (_vm.StatsBarCrowded && CompactStatsBar.Width > 0
                 && _tightStatsWidthDp + StatsBarSlackDp <= CompactStatsBar.Width)
        {
            _vm.StatsBarCrowded = false;
        }
#endif
        if (CompactStatsBar.IsVisible && CompactStatsBar.Width > 0)
        {
            var wanted = CompactStatsBar.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
            _vm.EffectsInBar = _vm.EffectsInBar
                ? wanted <= CompactStatsBar.Width + 0.5
                : wanted + EffectSlotsView.SlotsWidthDp + BarEffectsMarginDp + StatsBarSlackDp <= CompactStatsBar.Width;
        }
    }
}
