namespace Mucka.Core;

public class Profile
{
    public string Name { get; set; } = "MUD2";
    public string Host { get; set; } = "mud2.co.uk";
    public int Port { get; set; } = 23;
    public string AccountId { get; set; } = string.Empty;
    public bool RememberPassword { get; set; }
    public bool TelnetLoginEnabled { get; set; } = true;
    public string TelnetLoginName { get; set; } = "mud";
    public string[] Fkeys { get; set; } = new string[10];
}
