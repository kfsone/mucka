using Mucka.Core;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class ConnectPage : ContentPage
{
    private readonly ConnectViewModel _vm;
    private int _autoConnectAttempted;

    public ConnectPage(ConnectViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
        _vm.Connected += OnConnected;
        _vm.PasswordRequired = PromptPasswordAsync;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_vm.IsDirectConnectMode || Interlocked.Exchange(ref _autoConnectAttempted, 1) != 0)
        {
            return;
        }

        await _vm.LoadProfilesTask;
        if (_vm.ConnectCommand.CanExecute(null))
        {
            _vm.ConnectCommand.Execute(null);
        }
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

    private void OnConnected(MuckaConnection conn, Profile profile)
    {
        // Create GameViewModel on the UI thread so Dispatcher.CreateTimer() is available.
        // The lambda is async void (BeginInvokeOnMainThread takes Action) — any unhandled
        // exception here would propagate to the WinUI 3 dispatcher and crash the process
        // (0xc000027b), so we catch explicitly and surface the error instead.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var vm = _vm;
                Func<string[], Task>? saveFkeys = _vm.IsDirectConnectMode
                    ? null
                    : async fkeys => await vm.SaveProfileFkeysAsync(profile.Name, fkeys);
                Func<bool, Task>? saveMute = _vm.IsDirectConnectMode
                    ? null
                    : async mute => await vm.SaveProfileMuteAsync(profile.Name, mute);
                var gameVm = new GameViewModel(conn, profile, saveFkeys, saveMute);
                var gamePage = new GamePage(gameVm, _vm.IsDirectConnectMode);
                await Navigation.PushAsync(gamePage);
            }
            catch (Exception ex)
            {
                LogCrash("OnConnected", ex);
                await DisplayAlertAsync("Launch Error", $"Could not open the game screen:\n{ex.Message}", "OK");
            }
        });
    }

    private static void LogCrash(string context, Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "mucka-crash.txt");
            File.WriteAllText(path, $"{DateTimeOffset.Now:o}  [{context}]\n{ex}\n");
            System.Diagnostics.Trace.WriteLine($"[Mucka] crash log: {path}");
        }
        catch { }
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Profile p)
        {
            _vm.SelectProfileCommand.Execute(p);
        }
    }
}
