using System.Globalization;
using System.Text;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A stat value as the wire brackets it in C89 (mud2_FE4.txt: "89 00 00 Stamina.", "89 00 01
/// Maximum stamina.", "89 01 Score."): the code, a C99 colour, the digits, then a pop for each.
/// Transcribed from the wire table, e.g. <c>F4 9B 9B FF FF FE A5 FF FF "100" FF FF FF FF</c>.
/// Returned as a Latin-1 string so a test can splice it into prose and feed the lot through
/// <see cref="ParserHarness.Feed(string)"/> or <c>Encoding.Latin1</c>.
/// </summary>
internal static class StatCodes
{
    private static readonly string C99White = Latin1(0xFE, 0xA5, 0xFF, 0xFF);
    private static readonly string PopPop = Latin1(0xFF, 0xFF, 0xFF, 0xFF);

    public static string Stamina(string digits) => Latin1(0xF4, 0x9B, 0x9B, 0xFF, 0xFF) + C99White + digits + PopPop;
    public static string MaxStamina(string digits) => Latin1(0xF4, 0x9B, 0x9C, 0xFF, 0xFF) + C99White + digits + PopPop;
    public static string Score(string digits) => Latin1(0xF4, 0x9C, 0xFF, 0xFF) + C99White + digits + PopPop;

    public static string Stamina(int value) => Stamina(value.ToString(CultureInfo.InvariantCulture));
    public static string MaxStamina(int value) => MaxStamina(value.ToString(CultureInfo.InvariantCulture));

    private static string Latin1(params byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
