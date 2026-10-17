namespace Mucka.Sounds;

/// <summary>
/// Where each click of the metronome's bracket is STARTED, given where its non-silent content sits in
/// its file. The bracket is defined by the sounds, not the files: the pre-click's last non-silent
/// sample lands <c>gap</c> before the boundary and the after-click's first non-silent sample lands
/// <c>gap</c> after it, so the silence between them is <c>2 * gap</c> centred on the boundary.
/// </summary>
public static class BracketPlacement
{
    /// <summary>Milliseconds BEFORE the boundary to start the pre-click so that its last non-silent
    /// sample lands <paramref name="gapMs"/> before it. Without a span the file is assumed to start
    /// sounding at once and the gap is used as the lead.</summary>
    public static double PreLeadMs(double gapMs, ClipSpan? span)
        => span is { } s ? gapMs + s.AudibleEndMs : gapMs;

    /// <summary>Milliseconds AFTER the boundary to start the after-click so that its first non-silent
    /// sample lands <paramref name="gapMs"/> after it. Negative when the clip's leading silence is longer
    /// than the gap: it then has to start before the boundary.</summary>
    public static double AfterOffsetMs(double gapMs, ClipSpan? span)
        => span is { } s ? gapMs - s.AudibleStartMs : gapMs;
}
