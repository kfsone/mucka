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
/// <item>your command echo: the typed line and CR LF, no code (<c>"words</c>,
///   <c>tell lazlo "words</c>, <c>re "words</c>).</item>
/// <item>your say: <c>{09.00}Name the title says "{09.02}words^".^|</c>. On every one of your
///   own says in the log the verb is asks after a closing '?' and exclaims after a closing '!';
///   another Creature's say sometimes keeps says before either. The script uses the rule for
///   both.</item>
/// <item>your tell: <c>{09.00}You tell your listeners "{09.03}words^".^|</c>, seen after
///   <c>re "words</c> and after <c>tell echo "words</c>. <c>tell lazlo "words</c> is not in the log
///   and is given the same reply.</item>
/// <item>another Creature's unprompted line follows the standing prompt directly: the message, then
///   its prompt.</item>
/// <item>their say/ask/exclaim/whisper <c>{09.02}</c>; shout/yodel/holler <c>{09.01}</c>; tell
///   <c>Name tells you "{09.03}words^".^|</c>; distant voices read
///   <c>A male voice in the distance shouts ...</c>.</item>
/// <item>wordless scream or yodel: <c>{09.00}Name screams.^|</c>.</item>
/// <item>act: <c>{09.00}Firstname {09.04}text^^|</c>. Emote: <c>{09.00}Name {09.05.0n}waves.^^|</c>.</item>
/// <item>a wrapped message breaks with <c>|</c> inside its frames and closes on the last row. One
///   logged wrap breaks a 92-character row before a word that would have made it 98; the width
///   follows the negotiated terminal.</item>
/// <item>a name with a rainbow adjective: <c>Gwen the {90}{99.14}resilient{90.01} witch</c>.</item>
/// </list>
/// <para>Another Creature's yell, and a scream with words, are not in the wire log; they take the
/// shout shape with the verb changed. Names are real personas and creatures from the wire log;
/// every spoken and acted word is invented.</para>
/// </summary>
public static class ChatTestScript
{
    /// <summary>The title your say lines carry after your name.</summary>
    public const string MyTitle = "the warlock";

    private const string Prompt = "{01}{01.02}*^^";

    private enum Shape { MySay, MyTell, TheirLine }

    private readonly record struct Entry(Shape Shape, string Text);

    // Mine(words): your say. MyTell(command, words): your tell. Their(body): another Creature's line,
    // written in the shape notation above.
    private static Entry Mine(string words) => new(Shape.MySay, words);
    private static Entry MyTell(string command, string words) => new(Shape.MyTell, command + " \"" + words);
    private static Entry Their(string body) => new(Shape.TheirLine, body);

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
        Says("Crispybob the necromancer", "I have counted eleven rats in the cellar so far."),
        Mine("I left a lantern by the well, if anyone needs light."),
        Wordless("Drizzle the wobbly mage", "screams"),
        Cries("Crispybob the necromancer", "yells", "Get off my turnips!"),
        Emotes("Ringding the sorcerer", 0, "laughs."),
        MyTell("tell lazlo", "are you still near the mill? I have spare torches"),
        Cries("A male voice in the distance", "shouts", "the bridge troll is asleep, go now"),
        Tells("Lazlo the yeoman", "Yes, by the mill. Bring two if you can."),
        Mine("Mind the stepping stones, two of them wobble."),
        Says("Ringding the sorcerer", "Watch out for the loose plank on the jetty!"),
        Cries("Drizzle the wobbly mage", "yodels", "YODEL-AY-EE-OOO FROM THE HILLTOP!!!"),
        Says("The parrot", "Biscuits for the brave"),
        Mine("Whose goat is chewing my map?"),
        Acts("Crispybob", "tiptoes around the puddle very carefully."),
        Cries("Megatron the hero", "yells", "Over here, bring the rope"),
        Emotes("Titus the warlock", 2, "grins."),
        Wordless("A male voice in the distance", "screams"),
        Says("Godfrey the heroine", "I think the hermit moved his hut again."),
        Mine("Found it! It was in my other pocket all along!"),
        Cries("Drizzle the wobbly mage", "hollers", "WHERE ARE MY SPOONS!!!"),
        Says("Pippoz the warrior", "Which way to the orchard from here?"),
        Says("Megatron the hero", "The orchard is east, past the broken fence."),
        Wordless("A male voice in the distance", "yodels"),
        Mine("The swamp smells worse than usual today."),
        Emotes("Crispybob the necromancer", 1, "sighs."),
        Cries("Atomicbob the mage", "shouts", "Meet by the standing stones in ten"),
        Their("{09.00}Gwen the {90}{99.14}resilient{90.01} witch whispers \"{09.02}Keep your voice down near the crypt.^\".^"),
        Acts("Folly", "polishes an imaginary crown."),
        Mine("Anyone fancy a trip up to the cliffs later?"),
        Cries("A male voice in the distance", "yells", "is that smoke coming from the mill?"),
        Wordless("Folly the warlock", "screams"),
        Says("Titus the warlock", "My boots are still full of pond water."),
        Cries("Godfrey the heroine", "yodels", "Echo, echo, echo"),
        Tells("Atomicbob the mage", "Did you drop a blue feather by the bridge?"),
        MyTell("re", "no feather here, try asking the parrot"),
        Emotes("The thief", 2, "smiles."),
        Cries("A female voice in the distance", "shouts", "who keeps ringing that bell?"),
        Mine("Right, off to sort out that heap of rusty keys."),
        Says("Atomicbob the mage", "Somebody left the gate open again!"),
        Cries("A male voice in the distance", "hollers", "the tide is coming in fast"),
        Acts("Megatron", "counts the clouds out loud."),
        Their("{09.00}Wargames the necromancer says \"{09.02}I have been walking in circles around this forest"
              + "|for ages and every tree looks exactly like the one before it.^\".^"),
        Mine("Careful, there is something large in the reeds!"),
        Cries("Pippoz the warrior", "yells", "Stop throwing acorns at me"),
        Emotes("Chillipig the warlock", 2, "nods."),
        Cries("Folly the warlock", "shouts", "Lost: one boot, left foot, slightly damp"),
        Cries("A female voice in the distance", "yodels", "can anyone hear me down there?"),
        Mine("I will trade you a candle for that rope."),
        Says("Rintin the necromancer", "Has the rain stopped yet up on the hill?"),
        Says("Alexander the necromancer", "Not yet, it is coming down sideways."),
        Wordless("A female voice in the distance", "screams"),
        Cries("A male voice in the distance", "shouts", "found your boot, it was in the fountain"),
        Acts("Pippoz", "hides behind the nearest tree."),
        Mine("Back in a tick, the cat wants feeding."),
        Cries("Ringding the sorcerer", "hollers", "Last one to the tower buys the cider"),
        Emotes("Godfrey the heroine", 0, "giggles."),
        Cries("Titus the warlock", "yells", "The ferry is leaving without you"),
        Cries("Atomicbob the mage", "screams", "Something bit my ankle"),
        Mine("Does the ferry run when it is raining?"),
        Cries("Megatron the hero", "yodels", "Testing the valley acoustics"),
        Cries("Ringding the sorcerer", "shouts", "Thank you, kind stranger"),
        Acts("Godfrey", "hums a tune nobody recognises."),
        Emotes("Drizzle the wobbly mage", 2, "bows."),
        Says("Dara the sorceress", "I am going to brew something warm, back soon."),
        Cries("Alexander the necromancer", "hollers", "Supper is ready"),
        Mine("That went better than I expected, thanks all."),
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
        switch (entry.Shape)
        {
            case Shape.MySay:
                dsl.Append('"').Append(entry.Text).Append('#');
                dsl.Append("{09.00}").Append(myName).Append(' ').Append(MyTitle).Append(' ')
                   .Append(SayVerb(entry.Text)).Append(" \"{09.02}").Append(entry.Text).Append("^\".^|").Append(Prompt);
                break;
            case Shape.MyTell:
                var words = entry.Text[(entry.Text.IndexOf('"') + 1)..];
                dsl.Append(NotMyTarget(entry.Text, myName)).Append('#');
                dsl.Append("{09.00}You tell your listeners \"{09.03}").Append(words).Append("^\".^|").Append(Prompt);
                break;
            default:
                dsl.Append(NotMine(entry.Text, myName)).Append('|').Append(Prompt);
                break;
        }
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
