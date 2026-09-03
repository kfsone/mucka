using Microsoft.Maui.Graphics;
using System.ComponentModel;

namespace Mucka.ViewModels;

/// <summary>
/// One line of the side panel's "Here" list: a name FEI reported on the room floor, plus what the
/// client has been able to work out about it.
///
/// <para>FEI itself says only the name - see <see cref="MudSharp.Models.RoomCreatures"/> for why
/// nothing in the list distinguishes a rat from a vial, and where the distinction actually comes
/// from. <see cref="IsCreature"/> is fixed for the life of the row (the list is rebuilt whenever the
/// contents change); <see cref="IsEngaged"/> is not, because a fight starts and ends on a completely
/// different clock from the FEI heartbeat, and re-templating the whole list to flip one icon's colour
/// is exactly the UI-thread churn <c>OnFeiListComplete</c>'s diff exists to avoid (Invariant #1).</para>
/// </summary>
public sealed class RoomEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Objects: the same grey every other list in the panel uses.</summary>
    private static readonly Color ObjectColor = Color.FromArgb("#cccccc");

    /// <summary>Creatures. The owner's word is "red"; this is Campbell's bright red (#E74856), which
    /// is what the combat rail already draws every fact about an enemy in.</summary>
    private static readonly Color CreatureColor = Color.FromArgb("#E74856");

    /// <summary>The swords for a creature nothing is currently fighting. Grey rather than absent:
    /// its presence is the future attack affordance, and its colour is the only thing that says
    /// whether the fight is already on.</summary>
    private static readonly Color IdleSwordsColor = Color.FromArgb("#767676");

    public string Name { get; }

    private bool _isCreature;

    /// <summary>
    /// Whether the game has described this name as a creature in this room, or it is currently being
    /// fought. False for anything unclassified - the panel never guesses.
    ///
    /// <para>Settable because the evidence can arrive after the row does: a creature that walks in
    /// (C04.00.02) is described AFTER the FEI list that already listed it, and the refresh that
    /// follows reports the same names and is skipped by the list diff. Promoting the row in place is
    /// what stops the newcomer being drawn as a vial until the player next moves. It only ever goes
    /// false→true within a room; entering a room rebuilds the list from scratch.</para>
    /// </summary>
    public bool IsCreature
    {
        get => _isCreature;
        set
        {
            if (_isCreature == value) return;
            _isCreature = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCreature)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextColor)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSwords)));
        }
    }

    private bool _isEngaged;

    /// <summary>True while a fight against this exact instance name is open.</summary>
    public bool IsEngaged
    {
        get => _isEngaged;
        set
        {
            if (_isEngaged == value) return;
            _isEngaged = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEngaged)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwordsColor)));
        }
    }

    public Color TextColor => _isCreature ? CreatureColor : ObjectColor;

    /// <summary>The swords state, decided by <see cref="MudSharp.Models.HereRow"/> so the truth table
    /// is checkable without a MAUI runtime; this file only maps it onto colours.</summary>
    private MudSharp.Models.HereSwords Swords
        => MudSharp.Models.HereRow.SwordsFor(_isCreature, _isEngaged);

    public bool ShowSwords => Swords != MudSharp.Models.HereSwords.Hidden;

    public Color SwordsColor
        => Swords == MudSharp.Models.HereSwords.Engaged ? CreatureColor : IdleSwordsColor;

    public RoomEntry(string name, bool isCreature, bool isEngaged)
    {
        Name = name;
        _isCreature = isCreature;
        _isEngaged = isEngaged;
    }
}
