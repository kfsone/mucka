namespace Mucka.Core;

public class GameStats
{
    public int Stamina { get; set; }
    public int MaxStamina { get; set; }
    public int Strength { get; set; }
    public int MaxStrength { get; set; }
    public int Dexterity { get; set; }
    public int MaxDexterity { get; set; }
    public int Magic { get; set; }
    public int MaxMagic { get; set; }
    public long Score { get; set; }
    public bool Blind { get; set; }
    public bool Deaf { get; set; }
    public bool Crippled { get; set; }
    public bool Dumb { get; set; }
    public int MinutesToReset { get; set; }
    public char Weather { get; set; }
    public byte StaminaColour { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Dreamword { get; set; } = string.Empty;
}
