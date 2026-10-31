namespace Mucka.Pages;

/// <summary>The effect icons in fixed slots, for the two-row stats bar or the strip that floats below it.</summary>
public partial class EffectSlotsView : ContentView
{
    /// <summary>The slots' width: every column is a fixed one.</summary>
    public double SlotsWidthDp => Slots.ColumnDefinitions.Sum(c => c.Width.Value);

    public EffectSlotsView()
    {
        InitializeComponent();
    }
}
