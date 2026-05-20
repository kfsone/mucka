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
    }

    private void OnConnected(MudConnection conn, Profile profile)
    {
        // Create GameViewModel on the UI thread so Dispatcher.CreateTimer() is available.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var gameVm   = new GameViewModel(conn, profile);
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
