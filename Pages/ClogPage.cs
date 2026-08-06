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
/// active. There is deliberately no in-window "Combat" heading - the title bar already reads
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
    /// PROPORTIONAL font - which is why every carefully aligned column rendered ragged.</summary>
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
            Text = "- no combat data yet -",
            TextColor = ToneColor(ClogTone.Dim),
            FontSize = 12,
            FontAttributes = FontAttributes.Italic,
        };
        _empty.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.NoClogContent));

        // Dismisses a finished encounter's summary. It used to self-erase after 8 seconds, which was
        // too fast to actually read; it now persists until this is pressed.
        //
        // A bare glyph with no background fill, and FLOATED in a Grid cell shared with the readout
        // rather than stacked above it - as a stacked button it was a large grey block pushing the
        // whole panel down and dominating the top of the window.
        _clear = new Button
        {
            Text = "X",
            FontSize = 15,
            FontFamily = MonoFont,
            Padding = new Thickness(0),
            WidthRequest = 20,
            HeightRequest = 20,
            CornerRadius = 0,
            BorderWidth = 0,
            BackgroundColor = Colors.Transparent,
            TextColor = ToneColor(ClogTone.Dim),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
        };
        _clear.Clicked += OnClearClicked;
        _clear.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.CanClearCombatSummary));

        // The permanent "$clog eval <itemid>" hint (and the divider that sat under it) is gone: on a
        // 330x520 window it was fixed chrome eating ~6% of the height forever, for a command most
        // sessions never touch. See the report accompanying this change for a proposed
        // discoverability affordance in its place (not implemented here per the brief - no new UI
        // element without being asked for one).
        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                _empty,
                _readout,
            },
        };

        // Single-cell Grid so the clear glyph overlays the readout's top-right instead of occupying a
        // row of its own. Added last so it sits above the text in z-order.
        var overlay = new Grid { Children = { stack, _clear } };

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
            Content = overlay,
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
    /// idle - so "in combat" is visible from the window's edge without reading a word of it.</summary>
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
            Detach();
    }

    /// <summary>Deterministic teardown. MUST be called when the hosting window is destroyed.
    ///
    /// OnHandlerChanged(Handler is null) is NOT reliable for a page hosted in a secondary Window
    /// closed via Application.CloseWindow - the null-handler callback may never arrive, or arrive
    /// after the native content is already gone. When that happens the page stays subscribed to the
    /// view model, and the view model's delegate is what keeps the dead page alive: the next
    /// combat line raises ClogLines, this page renders into WinUI objects that have already been
    /// closed, and the process dies with a stowed exception wrapping RO_E_CLOSED (0x80000013,
    /// "the object has been closed"). That is a hard crash, not an exception we get to handle.
    ///
    /// It reproduces on the SECOND $clog on: the first window's page is still listening, so the
    /// first hit of the next fight renders into both the live page and the dead one.
    ///
    /// BindingContext is cleared too - _empty.IsVisible and _clear.IsVisible are live bindings and
    /// would fault exactly the same way. Idempotent: -= on an unsubscribed handler is a no-op, and
    /// this runs from both the window Destroying event and the handler callback.</summary>
    public void Detach()
    {
        _panel.PropertyChanged -= OnPanelPropertyChanged;
        _clear.Clicked -= OnClearClicked;
        BindingContext = null;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Belt and braces behind Detach(): never render into a page whose native views are gone.
        // If teardown is ever missed, rendering here would touch closed WinUI objects and take the
        // whole process down with RO_E_CLOSED - not an exception we can catch, and it costs the
        // user real in-game progress. A skipped render while detached is free: the window is
        // closed, and the 1 Hz tick repaints a live window on the next pass anyway.
        if (Handler is null)
            return;

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
