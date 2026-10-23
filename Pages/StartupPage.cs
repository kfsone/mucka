namespace Mucka.Pages;

/// <summary>
/// What the window shows while <see cref="Core.StartupSequence"/> runs: the page colour, and a line
/// saying so when an import is being merged, since that can take a while. Replaced by the profile
/// page when the sequence is done.
/// </summary>
internal sealed class StartupPage : ContentPage
{
    public StartupPage(string? importPath)
    {
        BackgroundColor = Color.FromArgb("#0C0C0C");
        NavigationPage.SetHasNavigationBar(this, false);
        if (importPath is null) return;
        Content = new Label
        {
            Text = $"Importing {Path.GetFileName(importPath)}...",
            TextColor = Color.FromArgb("#cccccc"),
            FontSize = 20,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
    }
}
