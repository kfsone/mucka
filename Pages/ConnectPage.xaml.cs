using Mucka.Core;
using Mucka.Core.GuidedLogin;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class ConnectPage : ContentPage
{
    private readonly ConnectViewModel _vm;
    private GamePage? _prebuiltGamePage;
    private GuidedLoginPage? _guidedPageForHandoff;
    private int _autoConnectAttempted;

    // -- Adaptive layout ------------------------------------------------------
    // Wide: the list in a fixed left column, the editor beside it (desktop, iPad, phone landscape).
    // Narrow: the list alone, and the editor on its own sheet. The large logo heads the list only when
    // the page is tall as well: in a phone's landscape it took the whole height and hid the list.
    // Size-based rather than OnIdiom so rotating a phone re-lays-out.
    private const double WideLayoutMinWidthDp = 600.0;
    private const double LargeLogoMinHeightDp = 700.0;
    private const double WideListColumnDp     = 340.0;
    private bool? _isWideLayout;
    private bool? _isTallLayout;

    public ConnectPage(ConnectViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
        WidePane.BindingContext = _vm;
        _vm.Connected += OnConnected;
        _vm.EditRequested += OnEditRequested;
        _vm.PasswordRequired = PromptPasswordAsync;
        WideVersionLabel.Text = NarrowVersionLabel.Text = $"v{AppInfo.VersionString}";
        NarrowWelcome.PresetChosen += _vm.BeginNewProfile;
        WideWelcome.PresetChosen += _vm.BeginNewProfile;
        NarrowWelcome.GuestRequested += OnGuestRequested;
        WideWelcome.GuestRequested += OnGuestRequested;
        WidePane.CloseRequested += (_, _) => _vm.CancelNewProfile();

        // Pick the initial layout from the display metrics BEFORE the first measure pass.
        try
        {
            var info = DeviceDisplay.Current.MainDisplayInfo;
            if (info.Density > 0)
                ApplyLayoutFor(info.Width / info.Density, info.Height / info.Density);
        }
        catch { /* no display info (unit tests, headless) - OnSizeAllocated will decide */ }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;
        _vm.PageWidthDp = width;
        ApplyLayoutFor(width, height);
    }

    private void ApplyLayoutFor(double width, double height)
    {
        var wide = width >= WideLayoutMinWidthDp;
        var tall = height >= LargeLogoMinHeightDp;
        if (_isWideLayout == wide && _isTallLayout == tall) return;
        _isWideLayout = wide;
        _isTallLayout = tall;
        ApplyLayout(wide, tall);
    }

    private void ApplyLayout(bool wide, bool tall)
    {
        RootGrid.ColumnDefinitions = wide
            ? new ColumnDefinitionCollection(
                new ColumnDefinition { Width = WideListColumnDp },
                new ColumnDefinition { Width = 1 },
                new ColumnDefinition { Width = GridLength.Star })
            : new ColumnDefinitionCollection(new ColumnDefinition { Width = GridLength.Star });
        Grid.SetColumnSpan(ListPane, 1);
        Separator.IsVisible = wide;
        RightPane.IsVisible = wide;
        var largeLogo = wide && tall;
        WideHeader.IsVisible = largeLogo;
        NarrowLogo.IsVisible = !largeLogo;
        NarrowVersionLabel.IsVisible = !largeLogo;
        _vm.IsWideLayout = wide;
        // The list highlights its row beside the editor; on its own it is only buttons.
        ProfileList.SelectionMode = wide ? SelectionMode.Single : SelectionMode.None;
        // The editor beside the list shows the error itself.
        ListStatus.MaximumHeightRequest = wide ? 0 : -1;
    }

    private async void OnAddClicked(object? sender, EventArgs e) => await ChooseQuickStartAsync(showGuest: false);

    // The guest note and the welcome both open Quick Start with its guest servers showing.
    private void OnGuestIntroClicked(object? sender, EventArgs e)
    {
        _vm.DismissGuestIntroCommand.Execute(null);
        OnGuestRequested();
    }

    private async void OnGuestRequested() => await ChooseQuickStartAsync(showGuest: true);

    private async void OnScoreHistoryClicked(object? sender, EventArgs e)
    {
        if (Navigation.ModalStack.Count > 0 || _vm.ScoreHistoryHost.Length == 0) return;
        await Navigation.PushModalAsync(new ScorePage(_vm.ScoreHistoryHost));
    }

    private bool _settingsOpen;

    /// <summary>Settings without a game running: what mucka.ini holds, saved back to it. A profile is
    /// only the server and the login; the rest is here.</summary>
    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        if (_settingsOpen || Navigation.ModalStack.Count > 0) return;
        _settingsOpen = true;
        try
        {
            var (settings, fkeys, name) = await _vm.LoadSettingsForEditorAsync();
            Func<ClientSettings, string[], Task>? onSave = _vm.IsDirectConnectMode
                ? null
                : (saved, keys) => _vm.SaveProfileSettingsAsync(name, saved, keys);
            var editorVm = new FkeyEditorViewModel(fkeys, settings,
                (applied, _) => UiFontScaleState.Current.Step = applied.UiFontStep, onSave);
            await Navigation.PushModalAsync(new FkeyEditorPage(editorVm), NavAnimation.ModalAnimated);
        }
        catch (Exception ex)
        {
            CrashLog.Write("OpenSettings", ex);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    /// <summary>Add: the Quick Start chooser, then the editor on the chosen preset.</summary>
    private async Task ChooseQuickStartAsync(bool showGuest)
    {
        if (Navigation.ModalStack.Count > 0) return;
        var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Navigation.PushModalAsync(new QuickStartPage(_vm, chosen, showGuest));
        if (await chosen.Task is { } key)
        {
            // The chooser is still on its way out: a sheet pushed now is lost.
            await Task.Delay(400);
            _vm.BeginNewProfile(key);
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Profile profile })
            await _vm.LaunchProfileAsync(profile);
    }

    private void OnEditClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Profile profile })
            _vm.BeginEdit(profile);
    }

    // Beside the list the editor is already showing the form; on its own it is a sheet.
    private async void OnEditRequested()
    {
        if (_isWideLayout == true || Navigation.ModalStack.Count > 0) return;
        await Navigation.PushModalAsync(new EditProfilePage(_vm));
    }

    /// <summary>What App's start-up found: an import outcome to report and whether the Text size page
    /// has been answered. Null when the start-up failed, which leaves both alone.</summary>
    internal StartupSequence.Result? Startup { get; set; }

    // The start-up questions, asked once. Held as a task because this page appears again whenever a
    // modal on it closes, and that appearance must wait for them too rather than run past them.
    private Task? _startupQuestions;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _startupQuestions ??= AskStartupQuestionsAsync();
        await _startupQuestions;
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

    // One modal at a time, from the first appearance of this page, so the window is already up:
    // the import's outcome, then the Text size page, then (Mac) where passwords are kept.
    // Each in its own try, so one that fails does not skip the ones after it.
    private async Task AskStartupQuestionsAsync()
    {
        await Task.Yield();
        try
        {
            if (Startup?.Import is { } import)
                await DisplayAlertAsync("Mucka import", import.Message, "OK");
        }
        catch (Exception ex)
        {
            CrashLog.Write("StartupImportAlert", ex);
        }
        try
        {
            if (Startup is { PromptFontSetup: true } startup)
                await RunFontSetupAsync(startup.Font, firstRun: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("FontSetup", ex);
        }
#if MACCATALYST
        if (!_vm.IsDirectConnectMode)
            await AskPasswordStorageAsync();
#endif
    }

    /// <summary>Opens the Text size page with <paramref name="initial"/> and saves what it returns -
    /// on Skip or Cancel, the sizes it opened with, which also records that it was answered.</summary>
    private async Task RunFontSetupAsync(Mucka.Ui.FontSetupChoice initial, bool firstRun)
    {
        if (firstRun)
        {
            // The plain size picker first: from here on the player can read this page's own words,
            // and Skip on the next page keeps what was picked here.
            var picked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Navigation.PushModalAsync(new InterfaceSizePage(initial.UiStep, picked));
            initial = initial with { UiStep = await picked.Task };
            UiFontScaleState.Current.Step = initial.UiStep;
#if DESKTOP
            FitWindowForWizard(initial.UiStep);
#endif
        }
        var (theme, palette) = await Task.Run(SettingsStore.LoadChatColoursAsync);
        var tcs = new TaskCompletionSource<FontSetupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Navigation.PushModalAsync(new FontSetupPage(initial, initial.UiStep, firstRun, palette, theme, tcs));
        var result = await tcs.Task ?? new FontSetupResult(initial, null);
        // After the load, which would otherwise overwrite it with what the file held before the save;
        // and applied even when the save fails, so this run uses what was chosen.
        await _vm.LoadProfilesTask;
        _vm.ApplyFontSetup(result.Sizes.GameFontPx, result.Theme);
        await Task.Run(() => SettingsStore.SaveFontSetupAsync(result.Sizes, result.Theme));
    }

#if DESKTOP
    // The Text Wizard laid out at the default step fits in this much window; a bigger step wants a
    // bigger window, up to most of the screen. The window only grows.
    private const double WizardWidthDp = 1000;
    private const double WizardHeightDp = 820;
    private const double WizardScreenShare = 0.9;

    private void FitWindowForWizard(int step)
    {
        if (Window is not { } window) return;
        var scale = Mucka.Ui.UiFontScale.ScaleFor(step);
#if MACCATALYST
        var screen = UIKit.UIScreen.MainScreen.Bounds;
        double screenW = screen.Width, screenH = screen.Height;
#else
        var info = DeviceDisplay.Current.MainDisplayInfo;
        if (info.Density <= 0) return;
        double screenW = info.Width / info.Density, screenH = info.Height / info.Density;
#endif
        var width = Math.Min(WizardWidthDp * scale, screenW * WizardScreenShare);
        var height = Math.Min(WizardHeightDp * scale, screenH * WizardScreenShare);
        if (window.Width < width) window.Width = width;
        if (window.Height < height) window.Height = height;
    }
#endif

    private bool _fontSetupOpen;

    /// <summary>The Text size button: the page again, opened with what the app runs at now.</summary>
    private async void OnFontSetupClicked(object? sender, EventArgs e)
    {
        // Set before the first await: a second click while the stored size is being read would
        // otherwise open a second page.
        if (_fontSetupOpen || Navigation.ModalStack.Count > 0) return;
        _fontSetupOpen = true;
        try
        {
            var fontPx = await Task.Run(() => SettingsStore.GetGlobalValueAsync("fontsize"));
            var current = new Mucka.Ui.FontSetupChoice(UiFontScaleState.Current.Step,
                int.TryParse(fontPx, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var px) && px > 0
                    ? px : GameViewModel.DefaultFontSizePx);
            await RunFontSetupAsync(current, firstRun: false);
        }
        catch (Exception ex)
        {
            CrashLog.Write("FontSetup", ex);
        }
        finally
        {
            _fontSetupOpen = false;
        }
    }

#if MACCATALYST
    // Never asked on the game pages. Until it is answered nothing is stored anywhere (see ProfileStore).
    private async Task AskPasswordStorageAsync()
    {
        try
        {
            if (await SettingsStore.GetSecurePasswordsAsync() is not null) return;
            var tcs = new TaskCompletionSource<bool>();
            await Navigation.PushModalAsync(new PasswordStorePage(tcs));
            var secure = await tcs.Task;
            await Task.Run(() => ProfileStore.ChooseSecureStorageAsync(secure, moveFromOld: true));
        }
        catch (Exception ex)
        {
            CrashLog.Write("PasswordStorageChoice", ex);
        }
    }
#endif

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
    /// Runs the guided-login state machine on its own full-window page, pushed onto the navigation
    /// stack (so it has the back button) and, on success, replaced by the game page directly: the
    /// profile page is not shown again once a connection has begun. <paramref name="conn"/>'s <see cref="GameViewModel"/> must already be constructed
    /// and subscribed before this runs, so it's listening to LineReady/GameModeEntered from the
    /// very start of the automated shell dance -- including the persona-selection response and the
    /// tearoom description that immediately follows it.
    /// </summary>
    private async Task<GuidedLoginResult> RunGuidedLoginOverlayAsync(
        MuckaConnection conn, Profile profile, Func<GamePage> buildGamePage)
    {
        var controller = new GuidedLoginController(conn, profile.GuidedLoginPersona);
        var vm = new GuidedLoginViewModel(controller);
        GuidedLoginPage? page = null;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                page = new GuidedLoginPage(vm, profile.FontSize);
                await Navigation.PushAsync(page, Mucka.Core.NavAnimation.Animated);
            });

            var result = await controller.RunAsync(page!.CancellationToken);
            // Built while the overlay is still up. Inflating the game page's XAML takes a noticeable
            // moment, most of all on the first run, and doing it after the overlay closes leaves the
            // profile page showing for that long.
            if (result.Outcome is GuidedLoginOutcome.Succeeded or GuidedLoginOutcome.ManualAtOptionMenu)
            {
                // Before the build: backing out while the game page is being built is then not
                // mistaken for cancelling a login that has already finished.
                page.HandOff();
                _guidedPageForHandoff = page;
                _prebuiltGamePage = await MainThread.InvokeOnMainThreadAsync(buildGamePage);
            }
            if (result.Outcome == GuidedLoginOutcome.Failed && result.FailureReason != null)
            {
                await DisplayAlertAsync(
                    "Persona Login Failed",
                    $"{result.FailureReason}\n\nYou may want to disable Persona Login for this profile and log in manually.",
                    "OK");
            }
            return result;
        }
        finally
        {
            vm.Detach();
            controller.Dispose();
            // Unless it is waiting to be replaced, or the player already backed out of it.
            if (page != null && !ReferenceEquals(page, _guidedPageForHandoff))
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (Navigation.NavigationStack.Contains(page))
                        await Navigation.PopAsync(Mucka.Core.NavAnimation.Animated);
                });
        }
    }

    private void OnConnected(MuckaConnection conn, Profile profile)    {
        // Create GameViewModel on the UI thread so Dispatcher.CreateTimer() is available.
        // The lambda is async void (BeginInvokeOnMainThread takes Action), so an unhandled exception
        // here reaches the platform's dispatcher. On Windows Platforms/Windows/App.xaml.cs marks it
        // Handled and the process survives; on Android there is no such handler, which is the real
        // exposure. Catch explicitly and surface the error instead.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var vm = _vm;
                Func<ClientSettings, string[], Task>? saveSettings = _vm.IsDirectConnectMode
                    ? null
                    : (settings, fkeys) => vm.SaveProfileSettingsAsync(profile.Name, settings, fkeys);
                // Separate, narrower delegate for the Combat Rail's own live-toggle persistence -
                // see GameViewModel's own remarks on why this cannot reuse saveSettings above.
                Func<bool, Task>? persistCombatRailVisibility = _vm.IsDirectConnectMode
                    ? null
                    : ConnectViewModel.PersistCombatRailVisibilityAsync;
                Func<bool, Task>? persistCombatStats = _vm.IsDirectConnectMode
                    ? null
                    : ConnectViewModel.PersistCombatStatsAsync;

                // GameViewModel subscribes to conn.LineReady/etc immediately, BEFORE GamePage is
                // pushed, so nothing is lost while guided login runs. GamePage is only pushed once
                // guided login has actually finished (or immediately, for non-guided profiles):
                // pushing it earlier and then covering it would trigger its OnDisappearing, which
                // ends the session (GamePage.EndSession -> GameViewModel.DisposeAsync ->
                // conn.DisposeAsync).
                var gameVm = new GameViewModel(conn, profile, saveSettings, persistCombatRailVisibility,
                    persistCombatStats);

                // Persona Login drives the shell after the client's own login; with MUD Login off the
                // player is logging in by hand, and the overlay would sit over the prompts they need.
                if (profile.GuidedLogin && profile.TelnetLoginEnabled)
                {
                    gameVm.BeginLoginFlow();
                    var result = await RunGuidedLoginOverlayAsync(
                        conn, profile, () => new GamePage(gameVm, _vm.IsDirectConnectMode));
                    gameVm.EndLoginFlow(result.MailItems);
                    if (result.Outcome is not (GuidedLoginOutcome.Succeeded or GuidedLoginOutcome.ManualAtOptionMenu))
                    {
                        await gameVm.DisposeAsync();
                        return;
                    }
                    if (result.Outcome == GuidedLoginOutcome.ManualAtOptionMenu)
                        gameVm.NoteLeftAtOptionMenu(result.MailItems);
                }

                var gamePage = _prebuiltGamePage ?? new GamePage(gameVm, _vm.IsDirectConnectMode);
                _prebuiltGamePage = null;
                await Navigation.PushAsync(gamePage, Mucka.Core.NavAnimation.Animated);
                if (_guidedPageForHandoff is { } handedOff)
                {
                    _guidedPageForHandoff = null;
                    // The player may have backed out of it already.
                    if (Navigation.NavigationStack.Contains(handedOff))
                        Navigation.RemovePage(handedOff);
                }
            }
            catch (Exception ex)
            {
                // A page built for this connection is not for the next one, and the login page must
                // not be left in the stack.
                _prebuiltGamePage = null;
                if (_guidedPageForHandoff is { } stranded)
                {
                    _guidedPageForHandoff = null;
                    if (Navigation.NavigationStack.Contains(stranded))
                        Navigation.RemovePage(stranded);
                }
                CrashLog.Write("OnConnected", ex);
                await DisplayAlertAsync("Launch Error", $"Could not open the game screen:\n{ex.Message}", "OK");
            }
        });
    }

}
