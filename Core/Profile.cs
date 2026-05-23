namespace Mucka.Core;

public class Profile
{
    private string[] _fkeys = CreateEmptyFkeys();

    public string Name { get; set; } = "MUD2";
    public string Host { get; set; } = "mud2.co.uk";
    public int Port { get; set; } = 23;
    public string AccountId { get; set; } = string.Empty;
    public bool RememberPassword { get; set; }
    public bool TelnetLoginEnabled { get; set; } = true;
    public string TelnetLoginName { get; set; } = "mud";
    public string[] Fkeys { get => _fkeys; set => _fkeys = NormalizeFkeys(value); }

    private static string[] CreateEmptyFkeys() => new string[36];

    private static string[] NormalizeFkeys(string[]? fkeys)
    {
        var normalized = CreateEmptyFkeys();
        if (fkeys == null)
            return normalized;

        for (var i = 0; i < normalized.Length; i++)
            normalized[i] = i < fkeys.Length ? fkeys[i] ?? string.Empty : string.Empty;

        return normalized;
    }
}
