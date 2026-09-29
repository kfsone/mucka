using System.ComponentModel;
using MudSharp.Models;

namespace Mucka.ViewModels;

/// <summary>
/// One example line in the chat colours preview, in the pane's three-part shape: the speaker's
/// framing (<see cref="Lead"/> and <see cref="Tail"/>, drawn in the Emotes row's face) around
/// the quoted words (<see cref="Quoted"/>, drawn in the owning row's face). An act or emote is all
/// lead. <see cref="Own"/> picks the row's own face or the other-Creature face, and the line follows
/// both rows live as their controls change. The text, and the lead's underlined runs
/// (<see cref="Lead0"/> .. <see cref="Lead4"/>, the odd ones underlined), are
/// <see cref="ChatExampleText"/>'s.
/// </summary>
public sealed class ChatExampleLine : BaseViewModel
{
    private readonly ChatColorEditorItem _emote;
    private readonly ChatColorEditorItem _words;

    public ChatExampleLine(ChatColorEditorItem emote, ChatColorEditorItem words, ChatExampleText text)
    {
        _emote   = emote;
        _words   = words;
        Own      = text.Own;
        Lead     = text.Lead;
        Quoted   = text.Quoted;
        Tail     = text.Tail;
        (Lead0, Lead1, Lead2, Lead3, Lead4) =
            (text.LeadRuns[0], text.LeadRuns[1], text.LeadRuns[2], text.LeadRuns[3], text.LeadRuns[4]);
        _emote.PropertyChanged += OnEmoteChanged;
        if (!ReferenceEquals(_words, _emote))
            _words.PropertyChanged += OnWordsChanged;
    }

    /// <summary>True for a line of your own, false for another Creature's.</summary>
    public bool Own { get; }

    public string Lead   { get; }
    public string Quoted { get; }
    public string Tail   { get; }

    public string Lead0 { get; }
    public string Lead1 { get; }
    public string Lead2 { get; }
    public string Lead3 { get; }
    public string Lead4 { get; }

    /// <summary>The line's width in characters, as the terminal font is monospaced.</summary>
    public int Length => Lead.Length + Quoted.Length + Tail.Length;

    /// <summary>The framing's colour: the Emotes row's, or on a shout or tell the words row's
    /// (<see cref="ChatColorizer.ColoursWholeLine"/>), dimmed by the Emotes row's face as the pane
    /// draws it.</summary>
    public Microsoft.Maui.Graphics.Color EmoteColor => ChatColorizer.ColoursWholeLine(_words.Part)
        ? _emote.ColorIn(Own, _words.Rgb)
        : Own ? _emote.OwnColor : _emote.OtherColor;
    public string EmoteFontFamily                   => Own ? _emote.OwnFontFamily : _emote.OtherFontFamily;
    public FontAttributes EmoteAttributes           => Own ? _emote.OwnAttributes : _emote.OtherAttributes;

    public Microsoft.Maui.Graphics.Color WordsColor => Own ? _words.OwnColor : _words.OtherColor;
    public string WordsFontFamily                   => Own ? _words.OwnFontFamily : _words.OtherFontFamily;
    public FontAttributes WordsAttributes           => Own ? _words.OwnAttributes : _words.OtherAttributes;

    private void OnEmoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (Slot(e.PropertyName))
        {
            case Face.Color:      OnPropertyChanged(nameof(EmoteColor)); break;
            case Face.FontFamily: OnPropertyChanged(nameof(EmoteFontFamily)); break;
            case Face.Attributes: OnPropertyChanged(nameof(EmoteAttributes)); break;
        }
        if (ReferenceEquals(_words, _emote))
            OnWordsChanged(sender, e);
    }

    private void OnWordsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (Slot(e.PropertyName))
        {
            case Face.Color:
                OnPropertyChanged(nameof(WordsColor));
                if (ChatColorizer.ColoursWholeLine(_words.Part))
                    OnPropertyChanged(nameof(EmoteColor));   // the framing is the words' colour
                break;
            case Face.FontFamily: OnPropertyChanged(nameof(WordsFontFamily)); break;
            case Face.Attributes: OnPropertyChanged(nameof(WordsAttributes)); break;
        }
    }

    private enum Face { None, Color, FontFamily, Attributes }

    // Which of this line's face properties a row's change touches: only the own or the other face,
    // whichever this line draws.
    private Face Slot(string? name) => name switch
    {
        nameof(ChatColorEditorItem.OwnColor)        when Own  => Face.Color,
        nameof(ChatColorEditorItem.OtherColor)      when !Own => Face.Color,
        nameof(ChatColorEditorItem.OwnFontFamily)   when Own  => Face.FontFamily,
        nameof(ChatColorEditorItem.OtherFontFamily) when !Own => Face.FontFamily,
        nameof(ChatColorEditorItem.OwnAttributes)   when Own  => Face.Attributes,
        nameof(ChatColorEditorItem.OtherAttributes) when !Own => Face.Attributes,
        _ => Face.None,
    };
}
