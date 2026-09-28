namespace MudSharp.Models;

/// <summary>
/// Which part of a C09 message a span belongs to, from the C09 frame that styled it
/// (mud2_FE4.txt). <see cref="Speaker"/> is the 09 00 frame - who did the thing - and every
/// act, emotion, affection and greeting frame (09 04 .. 09 10), which read as part of the doing.
/// The quoted words arrive in an inner 09 01 (shouted), 09 02 (said) or 09 03 (told) frame. In
/// captured wire, shouts, yells, yodels, screams, hollers and cheers all use 09 01, whispers 09 02, and
/// tells in both directions 09 03. Spans styled by any other code - a rainbow adjective inside a
/// name, say - are <see cref="None"/> even inside a message.
/// </summary>
public enum SpeechPart
{
    None,
    Speaker,
    Say,
    Shout,
    Tell,
}
