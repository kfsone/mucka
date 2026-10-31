namespace Mucka.Commands;

/// <summary>
/// The Unix login a connection presents (the SSH user, and the telnet NEW-ENVIRON USER). On the
/// real servers "mud" reaches the account prompt and "mudguest" logs in a guest; a profile keeps its
/// own name in mucka.ini (<c>loginname</c>) for a server that uses something else.
/// </summary>
public static class ShellLogin
{
    public const string Account = "mud";
    public const string Guest = "mudguest";

    /// <summary>The login to present. A MUD account login needs a non-guest shell, so with
    /// <paramref name="accountLogin"/> the guest name gives way to <see cref="Account"/>; any other
    /// stored name is used as it is, and a blank one is <see cref="Account"/>.</summary>
    public static string For(string? stored, bool accountLogin)
    {
        var name = string.IsNullOrWhiteSpace(stored) ? Account : stored.Trim();
        return accountLogin && string.Equals(name, Guest, StringComparison.OrdinalIgnoreCase) ? Account : name;
    }
}
