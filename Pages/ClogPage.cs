#if WINDOWS
using System.ComponentModel;
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// Windows-only floating "clog" window opened by "$clog on" (see GameViewModel.HandleClogCommand).
///
/// <para>Minimal chrome by design: a native title bar (for drag/close) and nothing else. The window
/// itself doubles as the on/off indicator: closing it (the native X) turns clogging back off (see
/// GamePage's Destroying handler), so there is exactly one place to look to know whether clogging is
/// active. There is deliberately no in-window "Combat" heading — the title bar already reads
/// "Mucka - Clog", so a heading was pure duplication.</para>
///
/// <para>The whole readout arrives pre-styled as <see cref="ClogLine"/>s from
/// CombatHistoryFormatter and renders into ONE Label's FormattedString. That keeps the layout to a
/// single native view and one text remeasure per genuine change; the view model only raises
/// PropertyChanged when the content actually differs, so the 1 Hz tick costs nothing when idle
/// (Invariant #1).</para>
/// </summary>
internal sealed class ClogPage : ContentPage
{
    private readonly SidePanelViewModel _panel;
    private readonly Label _readout;
    private readonly Label _empty;

    public ClogPage(GameViewModel vm)
    {
        BackgroundColor = Color.FromArgb("#0C0C0C");
        _panel = vm.SidePanel;
        BindingContext = _panel;

        _readout = new Label
        {
            FontSize = 11,
            FontFamily = "Cascadia Mono, Consolas, monospace",
            LineBreakMode = LineBreakMode.NoWrap,
            TextColor = ToneColor(ClogTone.Value),
        };

        _empty = new Label
        {
            Text = "— no combat data yet —",
            TextColor = ToneColor(ClogTone.Dim),
            FontSize = 12,
            FontAttributes = FontAttributes.Italic,
        };
        _empty.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.NoCombatData));

        var hint = new Label
        {
            Text = "$clog eval <itemid> — weigh/look/drop+get an item\nto measure its str/dex cost.",
            TextColor = ToneColor(ClogTone.Dim),
            FontSize = 10,
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            Padding = new Thickness(10),
            Children =
            {
                _empty,
                _readout,
                new BoxView { Color = Color.FromArgb("#2d333b"), HeightRequest = 1, Margin = new Thickness(0, 8, 0, 4) },
                hint,
            },
        };

        Content = new ScrollView { Content = stack };

        _panel.PropertyChanged += OnPanelPropertyChanged;
        Render(_panel.ClogLines);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Unsubscribe once the window is torn down, so a closed clog window does not keep receiving
        // (and re-rendering for) every combat line of the rest of the session.
        if (Handler is null)
            _panel.PropertyChanged -= OnPanelPropertyChanged;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SidePanelViewModel.ClogLines) or null))
            return;

        Render(_panel.ClogLines);
    }

    private void Render(IReadOnlyList<ClogLine> lines)
    {
        if (lines.Count == 0)
        {
            _readout.FormattedText = null;
            _readout.Text = string.Empty;
            _readout.IsVisible = false;
            return;
        }

        var formatted = new FormattedString();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                formatted.Spans.Add(new Span { Text = "\n" });

            var spans = lines[i].Spans;
            if (spans.Count == 0)
                continue;   // a blank spacer line: the newline above is the whole content

            foreach (var span in spans)
            {
                formatted.Spans.Add(new Span
                {
                    Text = span.Text,
                    TextColor = ToneColor(span.Tone),
                    TextDecorations = span.Strike ? TextDecorations.Strikethrough : TextDecorations.None,
                });
            }
        }

        _readout.FormattedText = formatted;
        _readout.IsVisible = true;
    }

    /// <summary>The single place tones become colours. Friendly/hostile carry what row labels used to,
    /// which is what freed up the width the first layout was spending on them.</summary>
    private static Color ToneColor(ClogTone tone) => tone switch
    {
        ClogTone.Dim => Color.FromArgb("#6e7681"),
        ClogTone.Friendly => Color.FromArgb("#58a6ff"),   // the player's side
        ClogTone.Hostile => Color.FromArgb("#f85149"),    // NPCs
        ClogTone.Good => Color.FromArgb("#3fb950"),       // beating the best on record
        ClogTone.Warn => Color.FromArgb("#d29922"),       // underperforming, or a live stat penalty
        ClogTone.Heading => Color.FromArgb("#a371f7"),
        _ => Color.FromArgb("#cccccc"),
    };
}
#endif
