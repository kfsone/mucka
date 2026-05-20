namespace Mucka.Core;

public class GameStats
{
    public int Stamina { get; set; }
    public int MaxStamina { get; set; }
    public int Strength { get; set; }
    public int Dexterity { get; set; }
    public long Score { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Dreamword { get; set; } = string.Empty;
}
