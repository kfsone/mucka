namespace Mucka.Commands;

/// <summary>
/// The dreamword, held across connections for as long as the world that handed it out.
///
/// <para>A dreamword belongs to one world: one server, between two resets. Nothing in the protocol
/// repeats it at login, so a relog - another persona, or the same one back after a drop - would
/// otherwise lose a word that is still live. The world is named by the server's own banner, "This
/// reset is number 127231.", which each login prints: the same number across relogs and across app
/// runs inside one reset, a new one after it, and a separate count per server
/// (<see cref="World"/>).</para>
///
/// <para>A word is only ever held under a world whose number was read, so a login whose banner could
/// not be read restores nothing.</para>
///
/// <para>Locked because each connection's Feed thread writes it, and a connection that is closing can
/// still be delivering its last lines while the next one reads its banner. A late write carries its
/// own world, so it can never be restored into a different one.</para>
/// </summary>
public sealed class DreamwordCarry
{
    /// <summary>One server's world between two resets. The host is compared without regard to case,
    /// so it is stored folded, and <see cref="Of"/> is the only way to make one.</summary>
    public readonly record struct World
    {
        private World(string host, int port, long reset)
        {
            Host = host;
            Port = port;
            Reset = reset;
        }

        public string Host { get; }
        public int Port { get; }
        public long Reset { get; }

        public static World Of(string host, int port, long reset)
            => new(host.Trim().ToLowerInvariant(), port, reset);
    }

    private readonly object _lock = new();
    private World? _world;
    private string? _word;

    /// <summary>A login's banner named <paramref name="world"/>. Returns the word held for it, or null;
    /// a different world drops whatever was held.</summary>
    public string? Enter(World world)
    {
        lock (_lock)
        {
            if (_world != world)
            {
                _world = world;
                _word = null;
            }
            return _word;
        }
    }

    /// <summary>The dreamword changed in <paramref name="world"/>; null when it was cleared.</summary>
    public void Note(World world, string? word)
    {
        lock (_lock)
        {
            _world = world;
            _word = word;
        }
    }
}
