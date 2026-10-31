using Mucka.ViewModels;
#if IOS
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
#endif

namespace Mucka.Pages;

/// <summary>The profile editor on its own sheet, for a window too narrow to put it beside the list.
/// It closes itself when the view model reports the edit finished (saved or deleted) and on Cancel.</summary>
internal sealed class EditProfilePage : ContentPage
{
    private readonly ConnectViewModel _vm;

    public EditProfilePage(ConnectViewModel vm)
    {
        _vm = vm;
        BackgroundColor = Color.FromArgb("#0C0C0C");
        BindingContext = vm;
        var editor = new ProfileEditView { BindingContext = vm, ShowClose = true };
        editor.CloseRequested += (_, _) =>
        {
            _vm.CancelNewProfile();
            Close();
        };
        Content = editor;
#if IOS
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.PageSheet);
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.EditFinished += Close;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.EditFinished -= Close;
        // Swiped away without saving: the new profile is dropped, as Cancel would.
        _vm.CancelNewProfile();
    }

    private async void Close()
    {
        if (Navigation.ModalStack.Contains(this))
            await MainThread.InvokeOnMainThreadAsync(() => Navigation.PopModalAsync());
    }
}
