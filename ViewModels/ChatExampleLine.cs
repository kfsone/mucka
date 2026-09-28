using System.ComponentModel;
using MudSharp.Models;

namespace Mucka.ViewModels;

/// <summary>
/// One example line in the chat colours preview, in the pane's three-part shape: the speaker's
/// framing (<see cref="Lead"/> and <see cref="Tail"/>, drawn in the Acts/Speaks row's face) around
/// the quoted words (<see cref="Quoted"/>, drawn in the owning row's face). An act or emote is all
/// lead. <see cref="Own"/> picks the row's own face or the other-Creature face, and the line follows
/// both rows live as their controls change. The text, and the lead's underlined runs
/// (<see cref="Lead0"/> .. <see cref="Lead4"/>, the odd ones underlined), are
/// <see cref="ChatExampleText"/>'s.
/// </summary>
public sealed class ChatExampleLine : BaseViewModel
{
    private readonly ChatColorEditorItem _speaker;
    private readonly ChatColorEditorItem _words;

    public ChatExampleLine(ChatColorEditorItem speaker, ChatColorEditorItem words, ChatExampleText text)
    {
        _speaker = speaker;
        _words   = words;
        Own      = text.Own;
        Lead     = text.Lead;
        Quoted   = text.Quoted;
        Tail     = text.Tail;
        (Lead0, Lead1, Lead2, Lead3, Lead4) =
            (text.LeadRuns[0], text.LeadRuns[1], text.LeadRuns[2], text.LeadRuns[3], text.LeadRuns[4]);
        _speaker.PropertyChanged += OnSpeakerChanged;
        if (!ReferenceEquals(_words, _speaker))
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

    public Microsoft.Maui.Graphics.Color SpeakerColor => Own ? _speaker.OwnColor : _speaker.OtherColor;
    public string SpeakerFontFamily                   => Own ? _speaker.OwnFontFamily : _speaker.OtherFontFamily;
    public FontAttributes SpeakerAttributes           => Own ? _speaker.OwnAttributes : _speaker.OtherAttributes;

    public Microsoft.Maui.Graphics.Color WordsColor => Own ? _words.OwnColor : _words.OtherColor;
    public string WordsFontFamily                   => Own ? _words.OwnFontFamily : _words.OtherFontFamily;
    public FontAttributes WordsAttributes           => Own ? _words.OwnAttributes : _words.OtherAttributes;

    private void OnSpeakerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (Slot(e.PropertyName))
        {
            case Face.Color:      OnPropertyChanged(nameof(SpeakerColor)); break;
            case Face.FontFamily: OnPropertyChanged(nameof(SpeakerFontFamily)); break;
            case Face.Attributes: OnPropertyChanged(nameof(SpeakerAttributes)); break;
        }
        if (ReferenceEquals(_words, _speaker))
            OnWordsChanged(sender, e);
    }

    private void OnWordsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (Slot(e.PropertyName))
        {
            case Face.Color:      OnPropertyChanged(nameof(WordsColor)); break;
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
