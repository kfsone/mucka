using Mucka.Core;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class ConnectPage : ContentPage
{
    private readonly ConnectViewModel _vm;

    public ConnectPage()
    {
        InitializeComponent();
        _vm = new ConnectViewModel();
        BindingContext = _vm;
        _vm.Connected += OnConnected;
        _vm.PasswordRequired = PromptPasswordAsync;
    }

    private async Task<PasswordResult?> PromptPasswordAsync(PasswordPromptArgs args)
    {
        var tcs = new TaskCompletionSource<PasswordResult?>();
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = new PasswordPage(args, tcs);
            await Navigation.PushModalAsync(page);
        });
        return await tcs.Task;
    }

    private void OnConnected(MudConnection conn, Profile profile)
    {
        // Create GameViewModel on the UI thread so Dispatcher.CreateTimer() is available.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var gameVm = new GameViewModel(conn, profile);
            var gamePage = new GamePage(gameVm);
            await Navigation.PushAsync(gamePage);
        });
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Profile p)
        {
            _vm.SelectProfileCommand.Execute(p);
        }
    }
}
