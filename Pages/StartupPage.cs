namespace Mucka.Pages;

/// <summary>
/// What the window shows while <see cref="Core.StartupSequence"/> runs: the page colour, and a line
/// saying so when an import is being merged, since that can take a while, and the maintenance overlay
/// for the wire heal and a schema upgrade, which also run before the profile page. Replaced by the
/// profile page when the sequence is done.
/// </summary>
internal sealed class StartupPage : ContentPage
{
    public StartupPage(string? importPath)
    {
        BackgroundColor = Color.FromArgb("#0C0C0C");
        NavigationPage.SetHasNavigationBar(this, false);
        var grid = new Grid();
        if (importPath is not null)
            grid.Children.Add(new Label
            {
                Text = $"Importing {Path.GetFileName(importPath)}...",
                TextColor = Color.FromArgb("#cccccc"),
                FontSize = 20,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            });
        grid.Children.Add(new MaintenanceOverlay());
        Content = grid;
    }
}
