using Mucka.ViewModels;
#if IOS
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
#endif

namespace Mucka.Pages;

/// <summary>The Add chooser: a handful of presets, each of which becomes an unsaved profile in the
/// editor, or for a guest server a connection that is never saved. Completes the task it is given with the chosen preset's key once it has closed, or
/// null when it was cancelled or swiped away.</summary>
public partial class QuickStartPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result;
    private string? _key;

    public QuickStartPage(ConnectViewModel vm, TaskCompletionSource<string?> result, bool showGuest = false)
    {
        InitializeComponent();
        _result = result;
        vm.SetGuestPresetsVisible(showGuest);
        BindingContext = vm;
#if IOS
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.PageSheet);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _result.TrySetResult(_key);
    }

    private async void OnPresetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;
        _key = key;
        await Navigation.PopModalAsync();
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();
}
