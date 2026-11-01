namespace Mucka.Commands;

/// <summary>Options for a guided-login pass. The initial connect uses the default mode, where a
/// profile's configured persona is logged straight in; a return to the Option menu during a
/// connection uses <see cref="ForReentry"/>, which never does.</summary>
/// <param name="KnownSlots">A persona list to offer before asking the shell for one, so the player
/// can choose while a reset is still rebuilding the database and be logged in once it finishes.
/// The choice is then <see cref="PreferredPersonaName"/> for the rest of the pass.</param>
public sealed record GuidedLoginOptions(
    string? PreferredPersonaName = null,
    bool StartAtOptionMenu = false,
    bool ForcePersonaChoice = false,
    bool AllowCreatePreferredPersona = true,
    TimeSpan? PlayRetryWindow = null,
    IReadOnlyList<PersonaSlot>? KnownSlots = null)
{
    /// <summary>How long a pass after a reset keeps asking for the persona list while the shell
    /// answers "The database is still initialising."</summary>
    public static readonly TimeSpan ResetRebuildWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The pass that follows a drop back to the Option menu, whatever the reason: the player always
    /// chooses the persona, so nothing logs them back in on its own.
    ///
    /// <para>After a reset the shell refuses "p" until the database is rebuilt. Given the persona
    /// list this connection last saw, the picker shows at once and the login waits for the rebuild;
    /// without one, the picker shows when the shell answers.</para>
    /// </summary>
    public static GuidedLoginOptions ForReentry(SessionDropReason reason, IReadOnlyList<PersonaSlot>? lastSlots)
    {
        var reset = reason == SessionDropReason.Reset;
        return new GuidedLoginOptions(
            StartAtOptionMenu: true,
            ForcePersonaChoice: true,
            AllowCreatePreferredPersona: false,
            PlayRetryWindow: reset ? ResetRebuildWindow : null,
            KnownSlots: reset && lastSlots is { Count: > 0 } ? lastSlots : null);
    }
}
