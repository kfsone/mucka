#if WINDOWS
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// Windows-only floating "clog" window opened by "$clog on" (see GameViewModel.HandleClogCommand).
/// Shows the same live combat-stats readout that used to live in the extras side panel — moved
/// here so it has room to grow (item-eval results, hidden-mechanic notes, etc.) without competing
/// for space with the always-busy side panel.
///
/// <para>Minimal chrome by design: a native title bar (for drag/close) and nothing else — no
/// custom buttons. The window itself doubles as the on/off indicator: closing it (the native ✕)
/// turns clogging back off (see GamePage's Destroying handler), so there is exactly one place
/// to look to know whether clogging is active.</para>
///
/// <para>Data-bound directly to GameViewModel.SidePanel's existing Combat* properties (added by
/// the earlier HUD-prototype work) — this page adds no new view-model surface, it only relocates
/// the presentation.</para>
/// </summary>
internal sealed class ClogPage : ContentPage
{
    public ClogPage(GameViewModel vm)
    {
        BackgroundColor = Color.FromArgb("#0C0C0C");
        BindingContext = vm.SidePanel;

        var stack = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(10) };

        var heading = new Label
        {
            Text = "Combat", TextColor = Color.FromArgb("#58a6ff"), FontSize = 13,
            FontAttributes = FontAttributes.Bold,
        };
        stack.Add(heading);

        var noData = new Label
        {
            Text = "\u2014 no combat data yet \u2014", TextColor = Color.FromArgb("#555555"),
            FontSize = 12, FontAttributes = FontAttributes.Italic,
        };
        noData.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.NoCombatData));
        stack.Add(noData);

        var data = new VerticalStackLayout { Spacing = 2 };
        data.SetBinding(IsVisibleProperty, nameof(SidePanelViewModel.HasCombatData));
        stack.Add(data);

        data.Add(Row("Weapon", nameof(SidePanelViewModel.CombatWeapon)));
        data.Add(Row("Targets", nameof(SidePanelViewModel.CombatTargets)));
        data.Add(RowPair("You", nameof(SidePanelViewModel.CombatYouHits), "hit", nameof(SidePanelViewModel.CombatYouMisses), "miss", nameof(SidePanelViewModel.CombatYouHitRate)));
        data.Add(RowPair("Them", nameof(SidePanelViewModel.CombatTheyHits), "hit", nameof(SidePanelViewModel.CombatTheyMisses), "miss", nameof(SidePanelViewModel.CombatTheyHitRate)));
        data.Add(Row("Damage done/taken", null, nameof(SidePanelViewModel.CombatDamageDone), nameof(SidePanelViewModel.CombatDamageTaken)));
        data.Add(Row("Tempo/dps", null, nameof(SidePanelViewModel.CombatDuration), nameof(SidePanelViewModel.CombatDps)));
        data.Add(Row("Sta", nameof(SidePanelViewModel.CombatStamina)));
        data.Add(Row("Str", nameof(SidePanelViewModel.CombatStrength)));
        data.Add(Row("Dex", nameof(SidePanelViewModel.CombatDexterity)));
        data.Add(Row("Mag", nameof(SidePanelViewModel.CombatMagic)));
        data.Add(Row("Carry", nameof(SidePanelViewModel.CombatCarry)));
        data.Add(Row("Level", nameof(SidePanelViewModel.CombatProgress)));

        var sep = new BoxView { Color = Color.FromArgb("#2d333b"), HeightRequest = 1, Margin = new Thickness(0, 8, 0, 4) };
        stack.Add(sep);

        var hint = new Label
        {
            Text = "$clog eval <itemid> — weigh/look/drop+get an item\nto measure its str/dex cost.",
            TextColor = Color.FromArgb("#6e7681"), FontSize = 10,
        };
        stack.Add(hint);

        Content = new ScrollView { Content = stack };
    }

    private static View Row(string label, string? valueProperty, string? valueA = null, string? valueB = null)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        grid.Add(Lbl(label, Color.FromArgb("#58a6ff")), 0, 0);

        View valueView;
        if (valueProperty != null)
        {
            var v = Lbl(string.Empty, Color.FromArgb("#cccccc"));
            v.SetBinding(Label.TextProperty, valueProperty);
            valueView = v;
        }
        else
        {
            var a = Lbl(string.Empty, Color.FromArgb("#cccccc"));
            a.SetBinding(Label.TextProperty, valueA);
            var b = Lbl(string.Empty, Color.FromArgb("#cccccc"));
            b.SetBinding(Label.TextProperty, valueB);
            valueView = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.End, Children = { a, b } };
        }
        valueView.HorizontalOptions = LayoutOptions.End;
        grid.Add(valueView, 1, 0);
        return grid;
    }

    private static View RowPair(string label, string hitsProp, string hitsSuffix, string missesProp, string missesSuffix, string rateProp)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        grid.Add(Lbl(label, Color.FromArgb("#58a6ff")), 0, 0);

        var formatted = new Label { FontSize = 11, HorizontalOptions = LayoutOptions.End };
        var span = new FormattedString();
        span.Spans.Add(BoundSpan(hitsProp, Color.FromArgb("#cccccc")));
        span.Spans.Add(new Span { Text = $" {hitsSuffix}  ", TextColor = Color.FromArgb("#6e7681") });
        span.Spans.Add(BoundSpan(missesProp, Color.FromArgb("#cccccc")));
        span.Spans.Add(new Span { Text = $" {missesSuffix}  ", TextColor = Color.FromArgb("#6e7681") });
        span.Spans.Add(BoundSpan(rateProp, Color.FromArgb("#cccccc")));
        formatted.FormattedText = span;
        grid.Add(formatted, 1, 0);
        return grid;
    }

    private static Span BoundSpan(string property, Color color)
    {
        var span = new Span { TextColor = color };
        span.SetBinding(Span.TextProperty, property);
        return span;
    }

    private static Label Lbl(string text, Color color) => new()
    {
        Text = text, TextColor = color, FontSize = 11,
        FontFamily = "Cascadia Mono, Consolas, monospace",
    };
}
#endif
