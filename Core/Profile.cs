namespace Mucka.Core;

public class Profile
{
    public string Name { get; set; } = "MUD2";
    public string Host { get; set; } = "mud2.co.uk";
    public int Port { get; set; } = 23;
    public string[] Fkeys { get; set; } = new string[10];
}
