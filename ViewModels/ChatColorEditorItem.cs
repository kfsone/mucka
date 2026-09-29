using System.Globalization;
using MudSharp.Models;

namespace Mucka.ViewModels;

/// <summary>
/// One chat colour row on the Display tab: the hex text the user types and three 0-255 channel
/// sliders, kept in step both ways; the own and other faces (italic, bold, dim) of the row's
/// <see cref="Part"/>; and what the preview draws them in. Typed text that does not parse leaves
/// the sliders and preview on the last valid colour, which is also what <see cref="Hex6"/> saves.
/// </summary>
public sealed class ChatColorEditorItem : BaseViewModel
{
    private const string UprightFamily = "Cascadia Mono";
    private const string ItalicFamily  = "Cascadia Mono Italic";

    private string _hex;
    private int _rgb;
    private ChatFace _own;
    private ChatFace _other;

    public ChatColorEditorItem(string label, SpeechPart part, ChatColorizer.Palette palette)
    {
        Label  = label;
        Part   = part;
        _rgb   = palette.RgbOf(part) ?? 0;
        _hex   = ChatColorizer.ToHex(_rgb);
        _own   = palette.Faces.Of(part, own: true);
        _other = palette.Faces.Of(part, own: false);
    }

    public string Label { get; }

    /// <summary>The message part this row colours and styles.</summary>
    public SpeechPart Part { get; }

    /// <summary>The example lines this row's setting covers, drawn beside its controls.</summary>
    public IReadOnlyList<ChatExampleLine> Examples { get; internal set; } = [];

    /// <summary>Whether <see cref="Examples"/> show beside this row; set with the editor's layout.</summary>
    public bool ShowExamples
    {
        get => _showExamples;
        internal set => Set(ref _showExamples, value);
    }
    private bool _showExamples;

    /// <summary>The colour as saved: 6 lowercase hex digits, no '#'.</summary>
    public string Hex6 => ChatColorizer.ToHex(_rgb);

    /// <summary>The colour as a packed 0xRRGGBB.</summary>
    public int Rgb => _rgb;

    /// <summary>This row's two faces written into <paramref name="faces"/>.</summary>
    public ChatFaces WriteFaces(ChatFaces faces) => faces.With(Part, true, _own).With(Part, false, _other);

    /// <summary>The row set to its part's colour and faces in <paramref name="palette"/>.</summary>
    public void Load(ChatColorizer.Palette palette)
    {
        _rgb   = palette.RgbOf(Part) ?? _rgb;
        _hex   = ChatColorizer.ToHex(_rgb);
        _own   = palette.Faces.Of(Part, own: true);
        _other = palette.Faces.Of(Part, own: false);
        OnPropertiesChanged(nameof(Hex), nameof(R), nameof(G), nameof(B), nameof(RText), nameof(GText), nameof(BText),
            nameof(Preview), nameof(OwnItalic), nameof(OwnBold), nameof(OwnDim),
            nameof(OtherItalic), nameof(OtherBold), nameof(OtherDim));
        RaiseFacePreviews();
    }

    public string Hex
    {
        get => _hex;
        set
        {
            if (!Set(ref _hex, value ?? string.Empty)) return;
            if (ChatColorizer.TryParseRgb(_hex) is int rgb && rgb != _rgb)
            {
                _rgb = rgb;
                OnPropertiesChanged(nameof(R), nameof(G), nameof(B), nameof(RText), nameof(GText), nameof(BText),
                    nameof(Preview));
                RaiseFacePreviews();
            }
        }
    }

    public double R { get => (_rgb >> 16) & 0xFF; set => SetChannel(16, value); }
    public double G { get => (_rgb >> 8)  & 0xFF; set => SetChannel(8,  value); }
    public double B { get => _rgb         & 0xFF; set => SetChannel(0,  value); }

    /// <summary>The swatch: the setting's colour, exactly.</summary>
    public Microsoft.Maui.Graphics.Color Preview => ToColor(_rgb);

    // The channel readouts beside the sliders; the reader's culture, as nothing parses them back.
    public string RText => ((int)R).ToString(CultureInfo.CurrentCulture);
    public string GText => ((int)G).ToString(CultureInfo.CurrentCulture);
    public string BText => ((int)B).ToString(CultureInfo.CurrentCulture);

    // The six face toggles.
    public bool OwnItalic   { get => Has(_own,   ChatFace.Italic); set => SetFace(ref _own,   ChatFace.Italic, value); }
    public bool OwnBold     { get => Has(_own,   ChatFace.Bold);   set => SetFace(ref _own,   ChatFace.Bold,   value); }
    public bool OwnDim      { get => Has(_own,   ChatFace.Dim);    set => SetFace(ref _own,   ChatFace.Dim,    value); }
    public bool OtherItalic { get => Has(_other, ChatFace.Italic); set => SetFace(ref _other, ChatFace.Italic, value); }
    public bool OtherBold   { get => Has(_other, ChatFace.Bold);   set => SetFace(ref _other, ChatFace.Bold,   value); }
    public bool OtherDim    { get => Has(_other, ChatFace.Dim);    set => SetFace(ref _other, ChatFace.Dim,    value); }

    // What the preview draws this row's spans in, on your own lines and on another Creature's:
    // the pane's colour (dim included), font face and weight.
    public Microsoft.Maui.Graphics.Color OwnColor   => ToColor(Has(_own,   ChatFace.Dim) ? ChatColorizer.Dim(_rgb) : _rgb);
    public Microsoft.Maui.Graphics.Color OtherColor => ToColor(Has(_other, ChatFace.Dim) ? ChatColorizer.Dim(_rgb) : _rgb);
    public string OwnFontFamily   => Has(_own,   ChatFace.Italic) ? ItalicFamily : UprightFamily;
    public string OtherFontFamily => Has(_other, ChatFace.Italic) ? ItalicFamily : UprightFamily;
    public FontAttributes OwnAttributes   => Has(_own,   ChatFace.Bold) ? FontAttributes.Bold : FontAttributes.None;
    public FontAttributes OtherAttributes => Has(_other, ChatFace.Bold) ? FontAttributes.Bold : FontAttributes.None;

    /// <summary>Another row's colour <paramref name="rgb"/> in this row's own or other face: dimmed
    /// when that face is dim.</summary>
    public Microsoft.Maui.Graphics.Color ColorIn(bool own, int rgb)
        => ToColor(Has(own ? _own : _other, ChatFace.Dim) ? ChatColorizer.Dim(rgb) : rgb);

    private static bool Has(ChatFace face, ChatFace flag) => (face & flag) != 0;

    private void SetFace(ref ChatFace face, ChatFace flag, bool on, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var next = on ? face | flag : face & ~flag;
        if (next == face) return;
        face = next;
        OnPropertyChanged(name);
        RaiseFacePreviews();
    }

    private void RaiseFacePreviews()
        => OnPropertiesChanged(nameof(OwnColor), nameof(OtherColor), nameof(OwnFontFamily), nameof(OtherFontFamily),
            nameof(OwnAttributes), nameof(OtherAttributes));

    private static Microsoft.Maui.Graphics.Color ToColor(int rgb)
        => Microsoft.Maui.Graphics.Color.FromRgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private void SetChannel(int shift, double value)
    {
        int v = (int)Math.Clamp(Math.Round(value), 0, 255);
        int rgb = (_rgb & ~(0xFF << shift)) | (v << shift);
        if (rgb == _rgb) return;
        _rgb = rgb;
        _hex = ChatColorizer.ToHex(rgb);
        OnPropertiesChanged(nameof(R), nameof(G), nameof(B), nameof(RText), nameof(GText), nameof(BText),
            nameof(Hex), nameof(Preview));
        RaiseFacePreviews();
    }
}
