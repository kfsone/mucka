namespace Mucka.Pages;

/// <summary>The effect icons in fixed slots, for the two-row stats bar or the strip that floats below it.</summary>
public partial class EffectSlotsView : ContentView
{
    /// <summary>The sum of the slot columns in EffectSlotsView.xaml.</summary>
    public const double SlotsWidthDp = 3 * 28 + 7 + 2 * 24 + 7 + 4 * 24;

    public EffectSlotsView()
    {
        InitializeComponent();
    }
}
