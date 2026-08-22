using System.Diagnostics;

namespace Mucka.Input;

/// <summary>
/// A stopwatch around the code that runs on a keystroke, with a budget it is expected to keep.
///
/// <para><b>Why measurement is part of the framework and not an afterthought.</b> The command box has
/// been broken three times, and every time the offending code looked cheap to the person adding it -
/// a binding round-trip, a notification chain, a layout pass. What they all had in common is that
/// nothing measured them, so the cost was invisible until it reached the owner's hands at 120 wpm.
/// A wall that stops you reaching the control is worth little if the code you write on this side of
/// it is allowed to be slow; this is the other half.</para>
///
/// <para><b>Always compiled in.</b> Two <c>Stopwatch.GetTimestamp()</c> calls per keystroke - tens of
/// nanoseconds against a budget measured in whole milliseconds - so there is no version of this the
/// player runs without. That is the point: the previous diagnostics were behind a build flag, which
/// meant the fault was only ever measurable by someone who already suspected it. A regression here
/// now announces itself in the ordinary build.</para>
///
/// <para>It reports and never intervenes. There is nothing useful to do about a slow keystroke after
/// the fact, and a framework that started dropping or deferring input to defend a budget would be a
/// worse bug than the one it was guarding against.</para>
/// </summary>
public sealed class InputPathBudget
{
    /// <summary>
    /// What a keystroke's handling is allowed to cost. 1 ms, chosen from the owner's own tolerance -
    /// conflicts on a 1-2 ms timescale are affordable, and anything approaching a frame is not. Note
    /// this budget covers only OUR code on the path (hotkey lookup, line accept); the control's own
    /// text handling is not ours to measure or to blame.
    /// </summary>
    public const double BudgetMilliseconds = 1.0;

    private readonly Action<string> _report;
    private readonly double _budgetMs;

    /// <param name="report">Where an overrun goes. Injected rather than wired to a logger here so
    /// this project stays dependency-free and testable.</param>
    public InputPathBudget(Action<string> report, double budgetMs = BudgetMilliseconds)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _budgetMs = budgetMs;
    }

    /// <summary>Overruns seen this session. Non-zero means something on the typing path is doing more
    /// than it should - check this before theorising about input lag.</summary>
    public long OverrunCount { get; private set; }

    /// <summary>The worst single overrun seen, in milliseconds. Useful because the shape of the
    /// problem differs: a steady 2 ms is a cost, an occasional 80 ms is a stall.</summary>
    public double WorstMilliseconds { get; private set; }

    /// <summary>
    /// Times <paramref name="work"/> and reports it if it exceeds the budget. Wrap the whole of a key
    /// handler in this, not parts of it - the question being asked is "what did this keystroke cost the
    /// player", and that is the sum, not any one contributor.
    /// </summary>
    public void Measure(string what, Action work)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        finally
        {
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms > _budgetMs)
            {
                OverrunCount++;
                if (ms > WorstMilliseconds)
                    WorstMilliseconds = ms;
                _report($"INPUT PATH BUDGET: {what} took {ms:F2}ms (budget {_budgetMs:F2}ms, "
                        + $"overrun #{OverrunCount}, worst {WorstMilliseconds:F2}ms)");
            }
        }
    }
}
