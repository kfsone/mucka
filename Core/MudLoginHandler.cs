namespace Mucka.Core;

/// <summary>
/// Handles MUD2 application-layer auto-login by scanning partial lines for known prompts
/// and sending the stored credentials. Deactivates permanently once the game mode is entered.
/// </summary>
/// <remarks>
/// Login sequence (WONT NEW_ENVIRON — shell login prompt IS presented):
///   1. "login:"    → send "mud"                   (Linux shell login)
///   2. "account id"→ send account ID              (MUD2 application)
///   3. "password"  → send password                (MUD2 application)
///   4. "Option"    → send ESC Ctrl-F ESC-T         (Clio client-mode entry, one-shot)
/// </remarks>
internal sealed class MudLoginHandler
{
    // Client-mode entry sequence from Clio telnet.l line 397:
    //   tx(tid,"\033\006\033-T",5)
    //   ESC(0x1B) Ctrl-F(0x06) ESC(0x1B) '-'(0x2D) 'T'(0x54)
    private static readonly byte[] ClientModeEntry = { 0x1B, 0x06, 0x1B, 0x2D, 0x54 };

    private readonly MuckaConnection _conn;
    private readonly string _accountId;
    private readonly string _password;

    private bool _active = true;
    private bool _accountSent;
    private bool _passwordSent;
    private bool _clientModeSent;

    public MudLoginHandler(MuckaConnection conn, string accountId, string password)
    {
        _conn = conn;
        _accountId = accountId;
        _password = password;

        conn.LineReady      += OnLineReady;
        conn.GameModeEntered += OnGameModeEntered;
    }

    /// <summary>Re-arm for a reconnect on the same connection.</summary>
    public void Reset()
    {
        _active = true;
        _accountSent = false;
        _passwordSent = false;
        _clientModeSent = false;
    }

    /// <summary>Unsubscribe events. Call before disposing the connection.</summary>
    public void Detach()
    {
        _conn.LineReady      -= OnLineReady;
        _conn.GameModeEntered -= OnGameModeEntered;
    }

    private void OnLineReady(MudSharp.Models.StyledLine line)
    {
        if (!_active || !line.IsPartial)
            return;

        var text = line.PlainText;

        // 1. Shell login prompt — send "mud" (Clio telnet.l line 352–361, clioluser = "mud")
        if (text.Contains("login:", StringComparison.OrdinalIgnoreCase))
        {
            _conn.SendLine("mud");
            return;
        }

        // 2. MUD2 account prompt
        if (!_accountSent && text.Contains("account id", StringComparison.OrdinalIgnoreCase))
        {
            _accountSent = true;
            _conn.SendLine(_accountId);
            return;
        }

        // 3. MUD2 password prompt
        if (!_passwordSent && text.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            _passwordSent = true;
            _conn.SendLine(_password);
            return;
        }

        // 4. Game selection menu — send client-mode entry once (Clio telnet.l line 390–403)
        if (!_clientModeSent && text.Contains("Option", StringComparison.Ordinal))
        {
            _clientModeSent = true;
            _conn.SendBytes(ClientModeEntry);
        }
    }

    private void OnGameModeEntered()
    {
        _active = false;
        Detach();
    }
}
