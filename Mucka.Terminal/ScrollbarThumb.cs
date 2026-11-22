namespace Mucka.Terminal;

/// <summary>
/// The scrollback scrollbar's thumb: where it sits for a scroll offset, and the offset a thumb
/// position means. An offset is rows scrolled up from the bottom; the bottom of the track is offset 0.
/// </summary>
public static class ScrollbarThumb
{
    /// <summary>The thumb's height: the viewport's share of the track, at least <paramref name="minimum"/>.</summary>
    public static float Height(float trackHeight, int viewportRows, int contentRows, float minimum)
        => Math.Max(minimum, trackHeight * viewportRows / contentRows);

    /// <summary>The top of the thumb at <paramref name="offset"/>.</summary>
    public static float Top(float trackHeight, float thumbHeight, int offset, int maxOffset)
    {
        float fromTop = maxOffset > 0 ? (float)(maxOffset - offset) / maxOffset : 1f;
        return fromTop * (trackHeight - thumbHeight);
    }

    /// <summary>The offset for a thumb whose top is at <paramref name="thumbTop"/>, clamped to the track.</summary>
    public static int Offset(float trackHeight, float thumbHeight, float thumbTop, int maxOffset)
    {
        float travel = trackHeight - thumbHeight;
        if (travel <= 0f || maxOffset <= 0)
            return 0;
        float fromBottom = 1f - Math.Clamp(thumbTop / travel, 0f, 1f);
        return (int)MathF.Round(fromBottom * maxOffset);
    }
}
