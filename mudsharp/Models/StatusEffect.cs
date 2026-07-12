namespace MudSharp.Models;

/// <summary>
/// A temporary magical effect on the local player, surfaced from the C11 (spell
/// start/end) protocol family. Hue-per-stat mirrors the game's own flash colours
/// (blue=STR, green=DEX, magenta=STA); Glow is a binary on/off with no negative.
/// </summary>
public enum StatusEffectKind
{
    Strength,
    Dexterity,
    Stamina,
    Glow,
    // Afflictions (blind/deaf/dumb/cripple) — carried only to supply the detected line as a
    // tooltip. Their on/off visibility is FES-authoritative (GameViewModel flags), NOT tracked here.
    Deaf,
    Blind,
    Dumb,
    Crippled,
}

/// <summary>
/// Which direction an effect pushes a stat. Buff and Debuff are independent slots —
/// a stat can carry both at once (e.g. +STR and −STR). Glow is always <see cref="Buff"/>.
/// </summary>
public enum EffectSign
{
    Buff,   // + : stronger / more adroit / fitter / glowing
    Debuff, // − : weaker / less adroit / less fit
}

/// <summary>
/// The kind of change reported by a single C11 bracket.
///
/// <para>NOTE: cast count and wear-off count do NOT match 1:1 — a single cast can apply
/// multiple levels, and they bleed off in stages ("Some of your magical X has worn off"
/// one or more times, then "Your magical X has worn off" for the final clear). So a
/// consumer should treat each (Kind, Sign) slot as present/absent rather than counting:
/// <see cref="Started"/> ⇒ present, <see cref="FullyWoreOff"/> ⇒ absent,
/// <see cref="PartiallyWoreOff"/> ⇒ still present (a candidate for a fade cue).</para>
/// </summary>
public enum EffectTransition
{
    Started,          // 11 02 enhance start / 11 00 glow
    PartiallyWoreOff, // 11 03 "Some of your magical X has worn off" — still active
    FullyWoreOff,     // 11 03 "Your magical X has worn off" / 11 01 unglow — now clear
}

/// <summary>
/// A change to one of the local player's temporary effects, emitted when a C11 bracket
/// is decoded. The stat identity and direction come from the phrase the code brackets
/// (the code itself is ambiguous — all six stat spells share <c>11 02</c>).
/// </summary>
public sealed record StatusEffectChange(
    StatusEffectKind Kind,
    EffectSign Sign,
    EffectTransition Transition,
    string? Message = null);   // the exact game line that produced this change (for tooltips)

/// <summary>
/// Immutable snapshot of which of the local player's effects are currently active.
/// Buff and Debuff for the same stat are independent — both can be true at once.
/// Present/absent only: message counts don't reliably give stack depth, so depth is
/// not tracked (see <see cref="EffectTransition"/>).
/// </summary>
public sealed record StatusEffectState(
    bool StrengthBuff  = false, bool StrengthDebuff  = false,
    bool DexterityBuff = false, bool DexterityDebuff = false,
    bool StaminaBuff   = false, bool StaminaDebuff   = false,
    bool Glow          = false,
    // The exact game line that turned each slot on — surfaced as the icon's tooltip.
    string? StrengthBuffMsg  = null, string? StrengthDebuffMsg  = null,
    string? DexterityBuffMsg = null, string? DexterityDebuffMsg = null,
    string? StaminaBuffMsg   = null, string? StaminaDebuffMsg   = null,
    string? GlowMsg          = null,
    // Affliction tooltip lines (visibility is FES-driven, not from these).
    string? DeafMsg = null, string? BlindMsg = null, string? DumbMsg = null, string? CrippledMsg = null)
{
    public static readonly StatusEffectState Empty = new();

    public bool AnyActive =>
        StrengthBuff || StrengthDebuff || DexterityBuff || DexterityDebuff
        || StaminaBuff || StaminaDebuff || Glow;
}
