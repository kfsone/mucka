namespace MudSharp.Combat;

/// <summary>
/// A stamina quantity that is known only to lie in a range: <c>Above &lt; x &lt;= AtMost</c>.
///
/// <para><b>The asymmetry is not a style choice</b> - it is the shape every constraint MUD2 actually
/// yields. "The creature was still standing after I dealt at least D" bounds the pool STRICTLY from
/// below; "it died on a blow that dealt at most H" bounds it INCLUSIVELY from above. Storing both
/// ends as inclusive would quietly claim a pool exactly equal to the damage that failed to kill it.</para>
///
/// <para><b><see cref="AtMost"/> is null for "no upper bound known"</b>, which is the ordinary state
/// for a creature nobody has managed to kill yet. Null must never be read as zero or as infinity-is-
/// fine: it means the evidence is one-sided, and callers are expected to say so rather than draw a
/// bar that runs off the end of the panel.</para>
///
/// <para>MUD2 stamina is integral - the game prints whole points and every damage bracket has integer
/// ends - so a printed "at least 90" converts to this form exactly, as <c>Above = 89</c>. See
/// <see cref="FromInclusive"/>, which is the only place that conversion is allowed to happen.</para>
/// </summary>
/// <param name="Above">Exclusive lower bound. 0 means "no lower bound worth stating" - a creature
/// that is standing has more than 0 stamina, which is true of every creature and informs nothing.</param>
/// <param name="AtMost">Inclusive upper bound, or null when nothing bounds it from above.</param>
public readonly record struct StaminaInterval(double Above, double? AtMost)
{
    /// <summary>Everything a positive stamina could be. The identity for <see cref="Intersect"/>.</summary>
    public static readonly StaminaInterval Unbounded = new(0, null);

    /// <summary>True when this says nothing at all - no lower bound and no upper one.</summary>
    public bool IsUnbounded => Above <= 0 && AtMost is null;

    /// <summary>True when the two ends cross, i.e. no value satisfies both. Reachable from real data:
    /// intersecting a pre-damaged creature's kill bracket with a healthy one's rung reading does it.
    /// Callers must decide what to keep rather than rendering an inverted band.</summary>
    public bool IsEmpty => AtMost is double top && top <= Above;

    /// <summary>Width of the band, or null when it is open at the top.</summary>
    public double? Width => AtMost is double top ? top - Above : null;

    /// <summary>Width as a fraction of the band's own midpoint, or null when open at the top - the
    /// figure that separates "we know this to within a few percent" from "we know it to within a
    /// factor of two". No threshold is applied here on purpose: what counts as tight enough is the
    /// caller's judgement, not this type's.</summary>
    public double? RelativeWidth
    {
        get
        {
            if (AtMost is not double top)
                return null;
            var middle = (Above + top) / 2.0;
            return middle <= 0 ? null : (top - Above) / middle;
        }
    }

    /// <summary>The midpoint, offered ONLY for a caller that has already decided it needs a single
    /// number and has said so at its own call site. Null when open at the top - there is no midpoint
    /// of a half-line, and returning the lower bound instead would be a fabricated measurement.</summary>
    public double? Midpoint => AtMost is double top ? (Above + top) / 2.0 : null;

    /// <summary>An inclusive range as the game printed it (<c>"between 90 and 99"</c>) in this type's
    /// half-open form. Exact rather than a widening, because stamina is integral: "at least 90" and
    /// "more than 89" describe the same set of whole numbers.</summary>
    public static StaminaInterval FromInclusive(double low, double high) => new(low - 1, high);

    /// <summary>The tightest interval satisfying both. May be <see cref="IsEmpty"/>; the caller
    /// decides which constraint to believe when it is.</summary>
    public StaminaInterval Intersect(StaminaInterval other)
    {
        var above = Math.Max(Above, other.Above);
        var atMost = (AtMost, other.AtMost) switch
        {
            (double a, double b) => Math.Min(a, b),
            (double a, null) => a,
            (null, double b) => b,
            _ => (double?)null,
        };
        return new StaminaInterval(above, atMost);
    }

    /// <summary>Whether <paramref name="value"/> satisfies this interval.</summary>
    public bool Contains(double value) => value > Above && (AtMost is not double top || value <= top);
}
