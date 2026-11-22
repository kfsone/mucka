using System.ComponentModel;
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// One chat example line, drawn as the pane draws it: in the terminal font, the lead and tail in
/// the Emotes row's face with a tell's sender and verb underlined, the quoted words in the owning
/// row's face. A span exists only for a run that has text, and each follows the line's colour and
/// face live.
/// </summary>
public sealed class ChatExampleLabel : Label
{
    private ChatExampleLine? _line;
    private readonly List<Span> _emote = [];
    private Span? _words;

    public ChatExampleLabel()
    {
        FontFamily = "Cascadia Mono";
        FontSize = FkeyEditorPage.ChatExampleFontSize;
        FontAutoScalingEnabled = false;
        LineBreakMode = LineBreakMode.NoWrap;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        Attach(BindingContext as ChatExampleLine);
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        base.OnHandlerChanging(args);
        if (args.NewHandler is null && _line is { } line)
            line.PropertyChanged -= OnLineChanged;
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is not null && _line is { } line)
        {
            line.PropertyChanged -= OnLineChanged;
            line.PropertyChanged += OnLineChanged;
            Apply();
        }
    }

    private void Attach(ChatExampleLine? line)
    {
        if (ReferenceEquals(line, _line)) return;
        if (_line is not null)
            _line.PropertyChanged -= OnLineChanged;
        _line = line;
        _emote.Clear();
        _words = null;
        if (line is null)
        {
            FormattedText = null;
            return;
        }

        var text = new FormattedString();
        for (var i = 0; i < line.LeadRuns.Count; i++)
            if (line.LeadRuns[i].Length > 0)
                text.Spans.Add(Run(line.LeadRuns[i], underline: i % 2 == 1, _emote));
        if (line.Quoted.Length > 0)
        {
            _words = Run(line.Quoted, underline: false, null);
            text.Spans.Add(_words);
        }
        if (line.Tail.Length > 0)
            text.Spans.Add(Run(line.Tail, underline: false, _emote));
        FormattedText = text;
        Apply();
        line.PropertyChanged += OnLineChanged;
    }

    private static Span Run(string text, bool underline, List<Span>? emote)
    {
        var span = new Span { Text = text, FontAutoScalingEnabled = false };
        if (underline)
            span.TextDecorations = TextDecorations.Underline;
        emote?.Add(span);
        return span;
    }

    private void Apply()
    {
        if (_line is not { } line) return;
        foreach (var span in _emote)
        {
            span.TextColor = line.EmoteColor;
            span.FontFamily = line.EmoteFontFamily;
            span.FontAttributes = line.EmoteAttributes;
        }
        if (_words is { } words)
        {
            words.TextColor = line.WordsColor;
            words.FontFamily = line.WordsFontFamily;
            words.FontAttributes = line.WordsAttributes;
        }
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e) => Apply();
}
