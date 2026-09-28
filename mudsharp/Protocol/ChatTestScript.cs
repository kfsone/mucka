using System.Text;
using MudSharp.Models;

namespace MudSharp.Protocol;

/// <summary>One <c>$CHATTEST</c> entry's output: its lines, ending at the script's prompt, and the sound assets it requests.</summary>
public sealed record ChatTestUnit(IReadOnlyList<StyledLine> Lines, IReadOnlyList<string> Sounds);

/// <summary>
/// The <c>$CHATTEST</c> script: a scripted run of chat, as the raw bytes the server sends, played
/// through a <see cref="MudStreamParser"/> of its own. Only its lines and sounds leave it; it sends
/// nothing. It starts in game mode with a prompt shown, as the live parser stands when a command
/// is typed.
///
/// <para>Wire shapes, from the <c>wire</c> table (inbound, and the outbound command for the echo).
/// Codes are written as in mud2_FE4.txt; <c>^</c> is a bare FF FF pop, <c>|</c> the server's
/// line end CR NUL CR LF:</para>
/// <list type="bullet">
/// <item>prompt: <c>{01}{01.02}*^^</c>. Every server message line is followed by one.</item>
/// <item>your command echo: the typed line and CR LF, no code (<c>"words</c>, <c>sh "words</c>,
///   <c>yell words</c>, <c>scream words</c>, <c>tell lazlo "words</c>, <c>re "words</c>,
///   <c>sigh</c>).</item>
/// <item>your say: <c>{09.00}Name the title says "{09.02}words^".^|</c>. On every one of your
///   own says in the log the verb is asks after a closing '?' and exclaims after a closing '!';
///   another Creature's say sometimes keeps says before either. The script uses the rule for
///   both.</item>
/// <item>your shout, yell or scream: <c>{09.00}You shout "{09.01}words^".^|</c>, the verb as
///   typed.</item>
/// <item>your tell: <c>{09.00}You tell your listeners "{09.03}words^".^|</c>, seen after
///   <c>re "words</c> and after <c>tell echo "words</c>. <c>tell lazlo "words</c> is not in the log
///   and is given the same reply.</item>
/// <item>your emote: <c>{09.00}OK, Name the title {09.05.01}sighs.^^|</c>.</item>
/// <item>another Creature's unprompted line follows the standing prompt directly: the message, then
///   its prompt.</item>
/// <item>their say/ask/exclaim/whisper <c>{09.02}</c>; shout/yodel/holler <c>{09.01}</c>; tell
///   <c>Name tells you "{09.03}words^".^|</c>; distant voices read
///   <c>A male voice in the distance shouts ...</c>.</item>
/// <item>wordless: <c>{09.00}Name screams.^|</c>, and screams, yodels, cheers or howls from a
///   distant voice.</item>
/// <item>act: <c>{09.00}Firstname {09.04}text^^|</c>. Emote: <c>{09.00}Name {09.05.0n}waves.^^|</c>.</item>
/// <item>an unseen Creature: <c>{09.00}Someone tells you "{09.03}words^".^|</c>;
///   <c>{09.00}Someone{09.07} bids you morning.^^|</c>, the space inside the hello frame;
///   <c>{09.00}Someone powerful {09.05.01}quacks.^^|</c>. No other C09 line from either label is in
///   the log.</item>
/// <item>a wrapped message breaks with <c>|</c> inside its frames and closes on the last row. One
///   logged wrap breaks a 92-character row before a word that would have made it 98; the width
///   follows the negotiated terminal.</item>
/// <item>a name with a rainbow adjective: <c>Gwen the {90}{99.14}resilient{90.01} witch</c>.</item>
/// </list>
/// <para>Names are real personas and creatures from the wire log; every spoken and acted word is
/// invented.</para>
/// </summary>
public static class ChatTestScript
{
    /// <summary>The title your say lines carry after your name.</summary>
    public const string MyTitle = "the warlock";

    private const string Prompt = "{01}{01.02}*^^";

    // Stands in a line of yours for your name and title.
    private const string MeToken = "@";

    // Echo: your typed command, null on another Creature's line. Body: the message in the shape
    // notation above.
    private readonly record struct Entry(string? Echo, string Body);

    private static Entry Mine(string words)
        => new("\"" + words, "{09.00}" + MeToken + " " + SayVerb(words) + " \"{09.02}" + words + "^\".^");
    private static Entry MyCry(string echo, string verb, string words)
        => new(echo, "{09.00}You " + verb + " \"{09.01}" + words + "^\".^");
    private static Entry MyTell(string command, string words)
        => new(command + " \"" + words, "{09.00}You tell your listeners \"{09.03}" + words + "^\".^");
    private static Entry MyEmote(string command, int kind, string verb)
        => new(command, "{09.00}OK, " + MeToken + " {09.05.0" + kind + "}" + verb + "^^");
    private static Entry Their(string body) => new(null, body);

    private static Entry Says(string who, string words)
        => Their("{09.00}" + who + " " + SayVerb(words) + " \"{09.02}" + words + "^\".^");
    private static Entry Cries(string who, string verb, string words)
        => Their("{09.00}" + who + " " + verb + " \"{09.01}" + words + "^\".^");
    private static Entry Wordless(string who, string verb) => Their("{09.00}" + who + " " + verb + ".^");
    private static Entry Tells(string who, string words) => Their("{09.00}" + who + " tells you \"{09.03}" + words + "^\".^");
    private static Entry Acts(string firstName, string text) => Their("{09.00}" + firstName + " {09.04}" + text + "^^");
    private static Entry Emotes(string who, int kind, string verb) => Their("{09.00}" + who + " {09.05.0" + kind + "}" + verb + "^^");

    private static string SayVerb(string words)
        => words.EndsWith('?') ? "asks" : words.EndsWith('!') ? "exclaims" : "says";

    private static readonly Entry[] Script =
    {
        Says("Drizzle the wobbly mage", "The tearoom scones have gone stale again."),
        Mine("Evening all, the kettle in the tearoom is still warm."),
        Emotes("Folly the warlock", 2, "waves."),
        Says("Folly the warlock", "Is the drawbridge up or down tonight?"),
        Mine("Has anyone seen where the ferryman went?"),
        Cries("Wargames the necromancer", "shouts", "ANYONE GOT A SPARE SHOVEL?"),
        Acts("Drizzle", "pretends to be a teapot, spout and all."),
        MyCry("sh \"Meet by the standing stones in ten", "shout", "Meet by the standing stones in ten"),
        Wordless("Drizzle the wobbly mage", "screams"),
        Emotes("Ringding the sorcerer", 0, "laughs."),
        MyTell("tell lazlo", "are you still near the mill? I have spare torches"),
        Cries("A male voice in the distance", "shouts", "the bridge troll is asleep, go now"),
        Tells("Lazlo the yeoman", "Yes, by the mill. Bring two if you can."),
        Says("Ringding the sorcerer", "Watch out for the loose plank on the jetty!"),
        Cries("Drizzle the wobbly mage", "yodels", "YODEL-AY-EE-OOO FROM THE HILLTOP!!!"),
        Tells("Someone", "your cloak has been inside out all evening"),
        MyCry("yell over here, bring the rope", "yell", "over here, bring the rope"),
        Acts("Crispybob", "tiptoes around the puddle very carefully."),
        Wordless("A male voice in the distance", "screams"),
        Their("{09.00}Someone{09.07} bids you morning.^^"),
        Mine("Found it! It was in my other pocket all along!"),
        Cries("Drizzle the wobbly mage", "hollers", "WHERE ARE MY SPOONS!!!"),
        Wordless("A male voice in the distance", "yodels"),
        Emotes("Crispybob the necromancer", 1, "gasps."),
        Their("{09.00}Gwen the {90}{99.14}resilient{90.01} witch whispers \"{09.02}Keep your voice down near the crypt.^\".^"),
        MyEmote("sigh", 1, "sighs."),
        Their("{09.00}Someone powerful {09.05.01}quacks.^^"),
        Tells("Atomicbob the mage", "Did you drop a blue feather by the bridge?"),
        MyTell("re", "no feather here, try asking the parrot"),
        Cries("A female voice in the distance", "shouts", "who keeps ringing that bell?"),
        Their("{09.00}Wargames the necromancer says \"{09.02}I have been walking in circles around this forest"
              + "|for ages and every tree looks exactly like the one before it.^\".^"),
        MyCry("scream something bit my ankle", "scream", "something bit my ankle"),
        Wordless("A male voice in the distance", "cheers"),
        Wordless("A female voice in the distance", "howls"),
        Tells("Someone", "guess who has been following you since the bridge?"),
        Mine("I will trade you a candle for that rope."),
        Says("Megatron the hero", "The orchard is east, past the broken fence."),
        Emotes("Titus the warlock", 2, "grins."),
        Acts("Megatron", "counts the clouds out loud."),
    };

    // Stand-ins for a scripted name that is the player's own, so no other Creature's line reads as
    // yours.
    private static readonly string[] SpareNames = { "Groggy", "Chillipig" };

    /// <summary>The whole script as the wire bytes, speaking as <paramref name="myName"/>.</summary>
    public static byte[] Bytes(string myName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(myName);
        var dsl = new StringBuilder();
        foreach (var entry in Script)
            AppendEntry(dsl, entry, myName);
        return Encode(dsl.ToString());
    }

    // One scripted entry in the shape notation: its echo if it has one, its message, its prompt.
    private static void AppendEntry(StringBuilder dsl, Entry entry, string myName)
    {
        if (entry.Echo is { } echo)
        {
            dsl.Append(NotMyTarget(echo, myName)).Append('#');
            dsl.Append(entry.Body.Replace(MeToken, myName + " " + MyTitle, StringComparison.Ordinal));
        }
        else
        {
            dsl.Append(NotMine(entry.Body, myName));
        }
        dsl.Append('|').Append(Prompt);
    }

    /// <summary>
    /// The script as <c>$CHATTEST slow</c> delivers it: one unit per scripted entry, in order, each
    /// holding the lines and sounds that entry makes - your echo with its message, a wrapped message
    /// with all its rows - and ending at the script's own prompt. Played through one fresh parser, so
    /// the complete lines are those <see cref="Play(string, Action{StyledLine}, Action{string})"/>
    /// emits.
    /// </summary>
    public static IReadOnlyList<ChatTestUnit> Units(string myName)
        => Units(new MudStreamParser(), myName);

    internal static IReadOnlyList<ChatTestUnit> Units(MudStreamParser parser, string myName)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentException.ThrowIfNullOrWhiteSpace(myName);
        parser.EnterGameMode();
        parser.Feed(Encode("{00}" + Prompt));
        var units = new List<ChatTestUnit>(Script.Length);
        var lines = new List<StyledLine>();
        var sounds = new List<string>();
        Action<StyledLine> onLine = lines.Add;
        Action<string> onSound = sounds.Add;
        parser.LineReady += onLine;
        parser.SoundRequested += onSound;
        try
        {
            foreach (var entry in Script)
            {
                var dsl = new StringBuilder();
                AppendEntry(dsl, entry, myName);
                parser.Feed(Encode(dsl.ToString()));
                units.Add(new ChatTestUnit(lines.ToArray(), sounds.ToArray()));
                lines.Clear();
                sounds.Clear();
            }
        }
        finally
        {
            parser.LineReady -= onLine;
            parser.SoundRequested -= onSound;
        }
        return units;
    }

    /// <summary>
    /// Plays the script through a fresh parser: <paramref name="lines"/> receives every line the
    /// parser emits, <paramref name="sounds"/> every sound asset it requests. The parser's outgoing
    /// bytes go nowhere.
    /// </summary>
    public static void Play(string myName, Action<StyledLine> lines, Action<string> sounds)
        => Play(new MudStreamParser(), myName, lines, sounds);

    internal static void Play(MudStreamParser parser, string myName, Action<StyledLine> lines, Action<string> sounds)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sounds);
        var script = Bytes(myName);
        parser.EnterGameMode();
        parser.Feed(Encode("{00}" + Prompt));
        parser.LineReady += lines;
        parser.SoundRequested += sounds;
        try
        {
            parser.Feed(script);
        }
        finally
        {
            parser.LineReady -= lines;
            parser.SoundRequested -= sounds;
        }
    }

    // A scripted line whose Creature has the player's name speaks under a spare one instead.
    private static string NotMine(string body, string myName)
    {
        foreach (var name in ScriptedFirstNames(body))
        {
            if (!string.Equals(name, myName, StringComparison.Ordinal)) continue;
            return body.Replace("{09.00}" + name + " ", "{09.00}" + Spare(myName) + " ", StringComparison.Ordinal);
        }
        return body;
    }

    // A scripted tell to the player's own name goes to the same spare, so the reply still pairs.
    private static string NotMyTarget(string command, string myName)
    {
        const string lead = "tell ";
        if (!command.StartsWith(lead, StringComparison.Ordinal)) return command;
        var end = command.IndexOf(' ', lead.Length);
        var target = command[lead.Length..end];
        if (!string.Equals(target, myName, StringComparison.OrdinalIgnoreCase)) return command;
        return lead + Spare(myName).ToLowerInvariant() + command[end..];
    }

    private static string Spare(string myName)
        => SpareNames.First(s => !string.Equals(s, myName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ScriptedFirstNames(string body)
    {
        const string lead = "{09.00}";
        if (!body.StartsWith(lead, StringComparison.Ordinal)) yield break;
        var end = body.IndexOf(' ', lead.Length);
        if (end > lead.Length) yield return body[lead.Length..end];
    }

    // The shape notation to bytes: {a.b..} is a C1 code - lead byte 0x9B+a, a parameter byte
    // 0x9B+n each, then the FF FF terminator; ^ is a bare FF FF pop; | is CR NUL CR LF; # is the
    // echo's CR LF. Anything else must be printable ASCII.
    internal static byte[] Encode(string dsl)
    {
        var bytes = new List<byte>(dsl.Length + 64);
        for (int i = 0; i < dsl.Length; i++)
        {
            char c = dsl[i];
            switch (c)
            {
                case '{':
                    int close = dsl.IndexOf('}', i);
                    if (close < 0) throw new FormatException($"unclosed code at {i}");
                    foreach (var part in dsl[(i + 1)..close].Split('.'))
                    {
                        int n = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                        if (n is < 0 or > 99) throw new FormatException($"code {n} at {i}");
                        bytes.Add((byte)(0x9B + n));
                    }
                    bytes.Add(0xFF); bytes.Add(0xFF);
                    i = close;
                    break;
                case '^': bytes.Add(0xFF); bytes.Add(0xFF); break;
                case '|': bytes.Add(0x0D); bytes.Add(0x00); bytes.Add(0x0D); bytes.Add(0x0A); break;
                case '#': bytes.Add(0x0D); bytes.Add(0x0A); break;
                case '}': throw new FormatException($"stray close at {i}");
                default:
                    if (c is < ' ' or > '~') throw new FormatException($"non-printable 0x{(int)c:x2} at {i}");
                    bytes.Add((byte)c);
                    break;
            }
        }
        return bytes.ToArray();
    }
}
