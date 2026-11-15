using System.Globalization;

namespace MudSharp.Models;

/// <summary>
/// A MUD2 C1 code as mud2_FE4.txt numbers it: the family (the lead byte, 0x9B = 00) and its first
/// parameter, which reads as 00 when the server sends none. "08 11" is <c>new C1Code(8, 11)</c>,
/// "Fight ends - flee."; "02" and "02 00" are the same code.
/// </summary>
public readonly record struct C1Code(int Family, int Sub)
{
    /// <summary>The lowest C1 lead byte: code 00.</summary>
    public const byte FirstLead = 0x9B;

    /// <summary>The code a lead byte and its first parameter byte make; a missing parameter is
    /// <see cref="FirstLead"/>, which reads as 00.</summary>
    public static C1Code Of(byte lead, byte firstParameter) => new(lead - FirstLead, firstParameter - FirstLead);

    /// <summary>As mud2_FE4.txt writes it: "08 11".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Family:00} {Sub:00}");
}
