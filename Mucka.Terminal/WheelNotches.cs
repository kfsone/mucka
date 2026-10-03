namespace Mucka.Terminal;

/// <summary>
/// Turns raw mouse-wheel deltas into scrollback rows, acting only on whole notches.
///
/// <para>A mouse wheel sends one full notch per click; a touchpad sends many small deltas.
/// Accumulating them means it takes a deliberate scroll to move the pane, rather than incidental
/// drift - and entering scrollback hides the command box, so a drift that tripped it would take the
/// keyboard away from the player.</para>
/// </summary>
public sealed class WheelNotches
{
    /// <summary>The delta of one wheel notch (Windows' WHEEL_DELTA).</summary>
    public const int NotchDelta = 120;

    /// <summary>Scrollback rows moved per notch.</summary>
    public const int RowsPerNotch = 3;

    private int _accumulated;

    /// <summary>Adds one wheel delta and returns the rows to scroll for the whole notches it
    /// completes: positive (wheel up) toward older output, negative toward the live bottom. The
    /// remainder carries to the next call.</summary>
    public int RowsFor(int delta)
    {
        _accumulated += delta;
        var notches = _accumulated / NotchDelta;
        _accumulated -= notches * NotchDelta;
        return notches * RowsPerNotch;
    }
}
