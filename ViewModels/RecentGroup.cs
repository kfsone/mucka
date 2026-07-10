using System.Collections.Generic;

namespace Mucka.ViewModels;

/// <summary>
/// One "minutes-since-last-seen" bucket in the side-panel Recent list — a heading line
/// (e.g. "~2 min") followed by the players last seen roughly that long ago. Rebuilt wholesale
/// by <see cref="SidePanelViewModel"/> whenever the Recent set or its ages change, so it needs
/// no change notification of its own.
/// </summary>
public sealed class RecentGroup
{
    public string Header { get; }
    public IReadOnlyList<WhoEntry> Members { get; }

    public RecentGroup(string header, IReadOnlyList<WhoEntry> members)
    {
        Header  = header;
        Members = members;
    }
}
