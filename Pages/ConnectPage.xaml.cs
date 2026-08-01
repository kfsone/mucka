using Mucka.Core;
using Mucka.Core.GuidedLogin;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class ConnectPage : ContentPage
{
    private readonly ConnectViewModel _vm;
    private int _autoConnectAttempted;

    // ── Adaptive layout ──────────────────────────────────────────────────────
    // Wide: logo+list in a fixed left column, form beside it (desktop, phone landscape).
    // Narrow: logo beside a height-capped scrollable list, form below (phone portrait).
    // Width-based rather than OnIdiom so rotating a phone re-lays-out.
    private const double WideLayoutMinWidthDp = 600.0;
    private const double NarrowLogoColumnDp   = 150.0;
    private const double NarrowListHeightDp   = 128.0;
    private bool? _isWideLayout;

    public ConnectPage(ConnectViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
        _vm.Connected += OnConnected;
        _vm.PasswordRequired = PromptPasswordAsync;
        VersionLabel.Text = $"v{AppInfo.VersionString}";
        TitleLabel.Text = $"mucka  v{AppInfo.VersionString}";

        // Pick the initial layout from the display metrics BEFORE the first measure pass:
        // CollectionView (Android RecyclerView) caches item sizes from its first measure, so
        // re-gridding it after startup leaves degenerate (unrendered) items behind.
        try
        {
            var info = DeviceDisplay.Current.MainDisplayInfo;
            if (info.Density > 0)
            {
                bool wide = (info.Width / info.Density) >= WideLayoutMinWidthDp;
                _isWideLayout = wide;
                if (!wide) ApplyLayout(wide);   // XAML default is already wide
                SizeLogo(wide, info.Height / info.Density);
            }
        }
        catch { /* no display info (unit tests, headless) — OnSizeAllocated will decide */ }
    }

    // The logo only gets its natural size when there's room for the list below it (tall, wide
    // screens — i.e. desktop). Narrow layouts and short screens (phone landscape) cap it.
    private void SizeLogo(bool wide, double heightDp)
        => LogoImage.HeightRequest = (!wide || heightDp < 500) ? 88 : -1;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;
        bool wide = width >= WideLayoutMinWidthDp;
        if (_isWideLayout != wide)
        {
            _isWideLayout = wide;
            ApplyLayout(wide);
        }
        SizeLogo(wide, height);
    }

    private void ApplyLayout(bool wide)
    {
        if (wide)
        {
            RootGrid.RowDefinitions = new RowDefinitionCollection(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star });
            RootGrid.ColumnDefinitions = new ColumnDefinitionCollection(
                new ColumnDefinition { Width = 256 },
                new ColumnDefinition { Width = 1 },
                new ColumnDefinition { Width = GridLength.Star });

            Grid.SetRow(HeaderPanel, 0);  Grid.SetColumn(HeaderPanel, 0);  Grid.SetColumnSpan(HeaderPanel, 1);
            Grid.SetRow(ProfileList, 1);  Grid.SetColumn(ProfileList, 0);  Grid.SetColumnSpan(ProfileList, 1);
            Grid.SetRow(Separator, 0);    Grid.SetRowSpan(Separator, 2);
            Grid.SetColumn(Separator, 1); Grid.SetColumnSpan(Separator, 1);
            Grid.SetRow(FormScroll, 0);   Grid.SetRowSpan(FormScroll, 2);
            Grid.SetColumn(FormScroll, 2); Grid.SetColumnSpan(FormScroll, 1);
            Grid.SetRow(TitleBar, 0);     Grid.SetColumn(TitleBar, 0);     Grid.SetColumnSpan(TitleBar, 1);

            TitleBar.IsVisible        = false;
            VersionLabel.IsVisible    = true;
            ProfileList.HeightRequest = -1;   // fill the star row
            Separator.WidthRequest    = 1;
            Separator.HeightRequest   = -1;
        }
        else
        {
            RootGrid.RowDefinitions = new RowDefinitionCollection(
                new RowDefinition { Height = GridLength.Auto },   // title strip
                new RowDefinition { Height = GridLength.Auto },   // logo + list band
                new RowDefinition { Height = 1 },                 // separator
                new RowDefinition { Height = GridLength.Star });  // form
            RootGrid.ColumnDefinitions = new ColumnDefinitionCollection(
                new ColumnDefinition { Width = NarrowLogoColumnDp },
                new ColumnDefinition { Width = GridLength.Star });

            Grid.SetRow(TitleBar, 0);     Grid.SetColumn(TitleBar, 0);     Grid.SetColumnSpan(TitleBar, 2);
            Grid.SetRow(HeaderPanel, 1);  Grid.SetColumn(HeaderPanel, 0);  Grid.SetColumnSpan(HeaderPanel, 1);
            Grid.SetRow(ProfileList, 1);  Grid.SetColumn(ProfileList, 1);  Grid.SetColumnSpan(ProfileList, 1);
            Grid.SetRow(Separator, 2);    Grid.SetRowSpan(Separator, 1);
            Grid.SetColumn(Separator, 0); Grid.SetColumnSpan(Separator, 2);
            Grid.SetRow(FormScroll, 3);   Grid.SetRowSpan(FormScroll, 1);
            Grid.SetColumn(FormScroll, 0); Grid.SetColumnSpan(FormScroll, 2);

            TitleBar.IsVisible        = true;
            VersionLabel.IsVisible    = false;   // version lives in the title strip here
            ProfileList.HeightRequest = NarrowListHeightDp;   // caps the Auto row; list scrolls within
            Separator.WidthRequest    = -1;
            Separator.HeightRequest   = 1;
        }

        // Re-gridding a live CollectionView leaves Android's RecyclerView with item sizes cached
        // from the old cell; resetting ItemsSource forces a rebuild. Deferred a dispatcher tick so
        // the rebuild measures against the NEW cell — resetting synchronously re-measures against
        // the outgoing layout and reproduces the bug on the wide→narrow flip. SavedProfiles is a
        // single ObservableCollection instance, so re-assigning it directly is binding-equivalent.
        if (ProfileList.Handler is not null)
        {
            Dispatcher.Dispatch(() =>
            {
                var src = ProfileList.ItemsSource;
                ProfileList.ItemsSource = null;
                ProfileList.ItemsSource = src;
            });
        }
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

    /// <summary>
    /// Runs the guided-login state machine as a modal overlay over the CURRENT page (ConnectPage
    /// at this point -- GamePage isn't pushed until guided login succeeds; see <see cref="OnConnected"/>
    /// for why). <paramref name="conn"/>'s <see cref="GameViewModel"/> must already be constructed
    /// and subscribed before this runs, so it's listening to LineReady/GameModeEntered from the
    /// very start of the automated shell dance -- including the persona-selection response and the
    /// tearoom description that immediately follows it.
    /// </summary>
    private async Task<bool> RunGuidedLoginOverlayAsync(MuckaConnection conn, Profile profile)
    {
        var controller = new GuidedLoginController(conn, profile.GuidedLoginPersona);
        var vm = new GuidedLoginViewModel(controller);
        GuidedLoginPage? page = null;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                page = new GuidedLoginPage(vm);
                await Navigation.PushModalAsync(page);
            });

            var result = await controller.RunAsync(page!.CancellationToken);

            if (result.Outcome == GuidedLoginOutcome.Succeeded)
                return true;

            if (result.Outcome == GuidedLoginOutcome.Failed && result.FailureReason != null)
            {
                await DisplayAlertAsync(
                    "Persona Login Failed",
                    $"{result.FailureReason}\n\nYou may want to disable Persona Login for this profile and log in manually.",
                    "OK");
            }
            return false;
        }
        finally
        {
            vm.Detach();
            controller.Dispose();
            if (page != null)
                await MainThread.InvokeOnMainThreadAsync(() => Navigation.PopModalAsync());
        }
    }

    // Blank → auto (0); a valid number is clamped by the VM setter; anything else restores display.
    private void OnMaxColumnsEntryCompleted(object? sender, EventArgs e)
    {
        var text = MaxColumnsEntry.Text;
        if (string.IsNullOrWhiteSpace(text))
            _vm.MaxColumns = 0;
        else if (int.TryParse(text, out var v))
            _vm.MaxColumns = v;
        // Reflect the (possibly clamped) value back — blank for auto, number otherwise.
        MaxColumnsEntry.Text = _vm.MaxColumnsText;
    }

    private void OnConnected(MuckaConnection conn, Profile profile)    {
        // Create GameViewModel on the UI thread so Dispatcher.CreateTimer() is available.
        // The lambda is async void (BeginInvokeOnMainThread takes Action) — any unhandled
        // exception here would propagate to the WinUI 3 dispatcher and crash the process
        // (0xc000027b), so we catch explicitly and surface the error instead.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var vm = _vm;
                Func<ClientSettings, string[], Task>? saveSettings = _vm.IsDirectConnectMode
                    ? null
                    : (settings, fkeys) => vm.SaveProfileSettingsAsync(profile.Name, settings, fkeys);

                // GameViewModel subscribes to conn.LineReady/etc immediately, BEFORE GamePage is
                // pushed, so nothing is lost while guided login runs -- pushing GamePage now (or
                // pushing the guided-login dialog modally on top of it) would trigger GamePage's
                // OnDisappearing, which disposes the connection (see GamePage.OnDisappearing ->
                // GameViewModel.DisposeAsync -> conn.DisposeAsync). GamePage is only pushed once
                // guided login has actually finished (or immediately, for non-guided profiles).
                var gameVm = new GameViewModel(conn, profile, saveSettings);

                if (profile.GuidedLogin)
                {
                    var ok = await RunGuidedLoginOverlayAsync(conn, profile);
                    if (!ok)
                    {
                        await gameVm.DisposeAsync();
                        return;
                    }
                }

                var gamePage = new GamePage(gameVm, _vm.IsDirectConnectMode);
                await Navigation.PushAsync(gamePage);
            }
            catch (Exception ex)
            {
                CrashLog.Write("OnConnected", ex);
                await DisplayAlertAsync("Launch Error", $"Could not open the game screen:\n{ex.Message}", "OK");
            }
        });
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Profile p)
        {
            _vm.SelectProfileCommand.Execute(p);
        }
    }

    private async void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is Profile profile)
        {
            await _vm.LaunchProfileAsync(profile);
        }
    }
}
