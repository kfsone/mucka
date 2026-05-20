namespace Mucka.Core;

/// <summary>
/// Resolved auto-login credentials for a MUSE (MUD2/MUD1) session.
/// Null fields mean "don't send for that step".
/// </summary>
/// <param name="EnvUser">Sent as the NEW-ENVIRON USER value during telnet negotiation (typically "mud").</param>
/// <param name="AccountId">Sent when the "Account ID:" prompt is seen.</param>
/// <param name="Password">Sent when the "Password:" prompt is seen (only after AccountId was sent).</param>
public record AutoLoginConfig(string? EnvUser, string? AccountId, string? Password);
