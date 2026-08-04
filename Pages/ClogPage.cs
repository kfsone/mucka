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
    /// <summary>The family name registered in MauiProgram (fonts.AddFont("CascadiaMono.ttf", ...)).
    /// MUST be exactly that alias: MAUI does NOT parse CSS-style fallback lists, so the previous
    /// "Cascadia Mono, Consolas, monospace" resolved to nothing and silently fell back to a
    /// PROPORTIONAL font — which is why every carefully aligned column rendered ragged.</summary>
    private const string MonoFont = "Cascadia Mono";

    private readonly SidePanelViewModel _panel;
    private readonly Label _readout;
    private readonly Label _empty;
    private readonly Border _frame;
    private readonly Button _clear;

    public ClogPage(GameViewModel vm)
    {
        BackgroundColor = Color.FromArgb("#0C0C0C");
        _panel = vm.SidePanel;
        BindingContext = _panel;

        _readout = new Label
        {
            FontSize = 11,
            FontFamily = MonoFont,
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
        _empty.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.NoClogContent));

        // Dismisses a finished encounter's summary. It used to self-erase after 8 seconds, which was
        // too fast to actually read; it now persists until this is pressed.
        _clear = new Button
        {
            Text = "clear",
            FontSize = 10,
            FontFamily = MonoFont,
            Padding = new Thickness(6, 0),
            HeightRequest = 22,
            BackgroundColor = Color.FromArgb("#21262d"),
            TextColor = ToneColor(ClogTone.Dim),
            HorizontalOptions = LayoutOptions.End,
        };
        _clear.Clicked += OnClearClicked;
        _clear.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.CanClearCombatSummary));

        var hint = new Label
        {
            Text = "$clog eval <itemid> — weigh/look/drop+get an item\nto measure its str/dex cost.",
            TextColor = ToneColor(ClogTone.Dim),
            FontSize = 10,
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                _clear,
                _empty,
                _readout,
                new BoxView { Color = Color.FromArgb("#2d333b"), HeightRequest = 1, Margin = new Thickness(0, 8, 0, 4) },
                hint,
            },
        };

        // Border rather than a pulsing animation: CLAUDE.md's Invariant #1 forbids repeating
        // UI-thread timers driving visual effects, because they compete with typing, and this window
        // shares the UI thread with the command box. A solid colour change carries the same "you are
        // in combat" signal for zero recurring cost. (Real pulsing would need to be driven on the
        // compositor/render thread, like the existing stale-dim behaviors.)
        _frame = new Border
        {
            Stroke = IdleStroke,
            StrokeThickness = 2,
            Padding = new Thickness(8),
            Content = stack,
        };

        Content = new ScrollView { Content = _frame };

        _panel.PropertyChanged += OnPanelPropertyChanged;
        Render(_panel.ClogLines);
        RefreshFrame();
    }

    private static readonly Color IdleStroke = Color.FromArgb("#2d333b");
    private static readonly Color CombatStroke = Color.FromArgb("#f85149");
    private static readonly Color GraceStroke = Color.FromArgb("#7d2b28");

    private void OnClearClicked(object? sender, EventArgs e) => _panel.ClearCombatSummaryCommand();

    /// <summary>Red while a fight is live, muted red through the post-kill grace window, neutral when
    /// idle — so "in combat" is visible from the window's edge without reading a word of it.</summary>
    private void RefreshFrame()
        => _frame.Stroke = _panel.InCombat
            ? (_panel.IsCombatGracePeriod ? GraceStroke : CombatStroke)
            : IdleStroke;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Unsubscribe once the window is torn down, so a closed clog window does not keep receiving
        // (and re-rendering for) every combat line of the rest of the session.
        if (Handler is null)
        {
            _panel.PropertyChanged -= OnPanelPropertyChanged;
            _clear.Clicked -= OnClearClicked;
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SidePanelViewModel.ClogLines):
                Render(_panel.ClogLines);
                break;
            case nameof(SidePanelViewModel.InCombat):
            case nameof(SidePanelViewModel.IsCombatGracePeriod):
                RefreshFrame();
                break;
            case null:
                Render(_panel.ClogLines);
                RefreshFrame();
                break;
        }
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
