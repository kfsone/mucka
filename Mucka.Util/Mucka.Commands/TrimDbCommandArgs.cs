using System.Globalization;

namespace Mucka.Commands;

/// <summary>What <c>$trimdb</c> was asked to do.</summary>
public enum TrimDbVerb
{
    /// <summary>Take probe traffic out of the wire log of runs that ended <c>Days</c> ago or more.</summary>
    Trim,
    /// <summary>Take the server's line wrapping out of runs that ended <c>Days</c> ago or more.</summary>
    Unwrap,
    /// <summary>Make the removal of rows trimmed <c>Days</c> ago or more final.</summary>
    Expire,
    /// <summary>Put every trimmed row back.</summary>
    Revert,
    /// <summary>Compact the database file.</summary>
    Optimize,
}

/// <summary>
/// The argument of <c>$trimdb trim [days]</c>, <c>unwrap [days]</c>, <c>expire [days]</c>,
/// <c>revert</c> and <c>optimize</c>. Pure text, so it is tested without the view model.
/// </summary>
public readonly record struct TrimDbCommandArgs(TrimDbVerb Verb, int Days)
{
    /// <summary>The most days either age accepts: ten years, so a typo cannot overflow the arithmetic.</summary>
    public const int MaxDays = 3650;

    /// <param name="argument">Everything after <c>$trimdb</c>.</param>
    /// <param name="defaultDays">Used by <c>trim</c>, <c>unwrap</c> and <c>expire</c> when no number is given.</param>
    public static TrimDbCommandArgs? Parse(string argument, int defaultDays, out string? error)
    {
        error = null;
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            error = "say what to do";
            return null;
        }

        TrimDbVerb verb;
        switch (words[0].ToLowerInvariant())
        {
            case "trim": verb = TrimDbVerb.Trim; break;
            case "unwrap": verb = TrimDbVerb.Unwrap; break;
            case "expire": verb = TrimDbVerb.Expire; break;
            case "revert": verb = TrimDbVerb.Revert; break;
            case "optimize": verb = TrimDbVerb.Optimize; break;
            default:
                error = $"'{words[0]}' is not one of trim, unwrap, expire, revert, optimize";
                return null;
        }

        var takesDays = verb is TrimDbVerb.Trim or TrimDbVerb.Unwrap or TrimDbVerb.Expire;
        if (words.Length > 2 || (words.Length == 2 && !takesDays))
        {
            error = takesDays ? "too many words" : $"{words[0]} takes no argument";
            return null;
        }
        if (words.Length == 2)
        {
            if (!int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                || days > MaxDays)
            {
                error = $"'{words[1]}' is not a number of days from 0 to {MaxDays}";
                return null;
            }
            return new TrimDbCommandArgs(verb, days);
        }
        return new TrimDbCommandArgs(verb, takesDays ? defaultDays : 0);
    }
}
