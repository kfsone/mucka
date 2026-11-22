namespace Mucka.Pages;

/// <summary>
/// A view that builds its content from <see cref="Template"/> the first time it is shown. A tab nobody
/// opens costs nothing when its page opens: MAUI creates the native views of every child of a page,
/// shown or not.
///
/// <para>The content is built on the UI thread's next turn rather than inside the change that showed
/// it, so whatever showed the view (a tab's button lighting) is drawn first.</para>
/// </summary>
public sealed class LazyView : ContentView
{
    public static readonly BindableProperty TemplateProperty = BindableProperty.Create(
        nameof(Template), typeof(DataTemplate), typeof(LazyView),
        propertyChanged: (view, _, _) => ((LazyView)view).BuildWhenShown());

    /// <summary>What the content is made from; its root is a <see cref="View"/>.</summary>
    public DataTemplate? Template
    {
        get => (DataTemplate?)GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    /// <summary>Raised once, when the content has been built.</summary>
    public event EventHandler? Built;

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsVisibleProperty.PropertyName)
            BuildWhenShown();
    }

    private void BuildWhenShown()
    {
        if (Content is not null || !IsVisible || Template is null)
            return;
        Dispatcher.Dispatch(() =>
        {
            if (Content is not null || !IsVisible || Template is not { } template)
                return;
            Content = (View)template.CreateContent();
            Built?.Invoke(this, EventArgs.Empty);
        });
    }
}
