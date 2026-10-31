namespace Mucka.Pages;

/// <summary>The profile page with no profiles. Raises <see cref="PresetChosen"/> with a Quick Start
/// key, and <see cref="GuestRequested"/> for the guest servers.</summary>
public partial class WelcomeView : ContentView
{
    public event Action<string>? PresetChosen;
    public event Action? GuestRequested;

    public WelcomeView()
    {
        InitializeComponent();
    }

    private void OnPresetClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string key })
            PresetChosen?.Invoke(key);
    }

    private void OnGuestClicked(object? sender, EventArgs e) => GuestRequested?.Invoke();
}
