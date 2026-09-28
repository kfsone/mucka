namespace MudSharp.Models;

/// <summary>The face a chat span is drawn in on top of its colour: any mix of italic, bold and dim.</summary>
[Flags]
public enum ChatFace
{
    None   = 0,
    Italic = 1,
    Bold   = 2,
    Dim    = 4,
}

/// <summary>
/// The faces of the four chat rows, one for your own messages and one for another Creature's.
/// A row's faces style the spans of its <see cref="SpeechPart"/>: the Speaker row the label and the
/// act/emote text alike, the Say, Shout and Tell rows the quoted words. They are looked up when a
/// span is drawn (<see cref="ChatColorizer.Palette.FaceOf"/>), never stored on it.
/// </summary>
public readonly record struct ChatFaces(
    ChatFace SpeakerOwn, ChatFace SpeakerOther,
    ChatFace SayOwn,     ChatFace SayOther,
    ChatFace ShoutOwn,   ChatFace ShoutOther,
    ChatFace TellOwn,    ChatFace TellOther)
{
    /// <summary>The defaults: the words of your own say, shout and tell italic, nothing else styled.</summary>
    public static readonly ChatFaces Default = new(
        SpeakerOwn: ChatFace.None,   SpeakerOther: ChatFace.None,
        SayOwn:     ChatFace.Italic, SayOther:     ChatFace.None,
        ShoutOwn:   ChatFace.Italic, ShoutOther:   ChatFace.None,
        TellOwn:    ChatFace.Italic, TellOther:    ChatFace.None);

    /// <summary>The face of a part on your own (<paramref name="own"/>) or another Creature's message;
    /// <see cref="ChatFace.None"/> for <see cref="SpeechPart.None"/>.</summary>
    public ChatFace Of(SpeechPart part, bool own) => part switch
    {
        SpeechPart.Speaker => own ? SpeakerOwn : SpeakerOther,
        SpeechPart.Say     => own ? SayOwn     : SayOther,
        SpeechPart.Shout   => own ? ShoutOwn   : ShoutOther,
        SpeechPart.Tell    => own ? TellOwn    : TellOther,
        _                  => ChatFace.None,
    };

    /// <summary>A copy with one row side's face replaced.</summary>
    public ChatFaces With(SpeechPart part, bool own, ChatFace face) => (part, own) switch
    {
        (SpeechPart.Speaker, true)  => this with { SpeakerOwn   = face },
        (SpeechPart.Speaker, false) => this with { SpeakerOther = face },
        (SpeechPart.Say,     true)  => this with { SayOwn       = face },
        (SpeechPart.Say,     false) => this with { SayOther     = face },
        (SpeechPart.Shout,   true)  => this with { ShoutOwn     = face },
        (SpeechPart.Shout,   false) => this with { ShoutOther   = face },
        (SpeechPart.Tell,    true)  => this with { TellOwn      = face },
        (SpeechPart.Tell,    false) => this with { TellOther    = face },
        _                           => this,
    };
}
