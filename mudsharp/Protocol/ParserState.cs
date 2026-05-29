namespace MudSharp.Protocol;

internal enum ParserState
{
    Normal,         // baseline: accumulating text

    // First 0xFF byte in Normal: disambiguates bare C255 pop from telnet IAC
    Ff1,            // received first 0xFF in Normal state (may be C255 pop or telnet IAC)

    // Telnet IAC sequences
    Iac,            // received 0xFF (IAC)
    IacDo,          // received IAC DO
    IacDont,        // received IAC DONT
    IacWill,        // received IAC WILL
    IacWont,        // received IAC WONT
    IacSb,          // received IAC SB (start subnegotiation)
    IacSbData,      // accumulating subnegotiation data
    IacSbIac,       // received IAC inside subnegotiation (possible IAC SE)

    // ANSI escape sequences
    Escape,         // received 0x1B
    EscapeBracket,  // received ESC [  (CSI)
    CsiParam,       // accumulating CSI parameter bytes
    EscapeDash,     // received ESC - (MUD2 shell command prefix, server-to-client)
    EscapeDashWidth, // received ESC - i (set-width; accumulating decimal digit(s) before final letter)

    // MUD2 C1 proprietary protocol (0x9B-0xFE)
    C1Seq,          // received C1 lead byte
    C1Data,         // accumulating C1 payload
    C1Ff1,          // received first 0xFF in C1 terminator
    // C1 sub-states handled by Mud2C1Decoder
    FesData,        // after C12+C08+C01+C255: collecting raw FES text line (until '\n')
    FewPlayerData,  // after WHO-list color code (C04/C05+C00/C01+C06+C255): collecting player name
    DreamwordData,  // after C15+C00+C00+C255: collecting [a-z]{1,14} dreamword letters
    C95Data,        // after C95+C255: collecting 5 newline-terminated client-mode lines
    C95LogoutLine,  // after C95+C03+C255: consuming 1 line (account-logout, silent)

}
