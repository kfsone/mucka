namespace MudSharp.Protocol;

/// <summary>Which of the player's stats a C89 code brackets (mud2_FE4.txt: "89 00 00 Stamina.",
/// "89 00 01 Maximum stamina.", "89 01 Score."). See <see cref="C1Scope.StatValue"/>.</summary>
internal enum StatField
{
    None,
    Stamina,
    MaxStamina,
    Score,
}
