using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class FkeyEditorPage : ContentPage
{
    private readonly FkeyEditorViewModel _vm;
#if UIKIT
    private MacEscape.Registration? _macEscape;
#endif

#if DESKTOP
    // The picker's file-type list, built once: every ".ini" import rebuilt the same two strings.
    private static readonly string[] IniFileTypes = [".ini", "*"];

    // Mac Catalyst filters by uniform type identifier, not extension.
    private static readonly string[] IniUniformTypes = ["public.text", "public.data"];

    /// <summary>The Function Keys tab's Import button is there.</summary>
    public const bool CanImport = true;
#else
    public const bool CanImport = false;
#endif

    // Narrower than this, the tab bar cannot hold every tab: Function Keys, Nav Keys and Data go to
    // the overflow button's menu. The same width the profile page goes narrow at.
    private const double NarrowMaxWidthDp = 600.0;

    private const string FunctionKeysChoice = "Function Keys";
    private const string NavKeysChoice = "Nav Keys";
    private const string DataChoice = "Data";

    private bool? _narrow;

    /// <summary>The chat examples' font size, as the XAML draws them.</summary>
    public const double ChatExampleFontSize = 14;

    /// <summary>The padding of the pane-coloured box the chat examples sit in.</summary>
    public static readonly Thickness ChatExamplePadding = new(10, 8);

    public FkeyEditorPage(FkeyEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;
        var narrow = width < NarrowMaxWidthDp;
        if (_narrow == narrow) return;
        _narrow = narrow;
        FunctionKeysTab.IsVisible = NavKeysTab.IsVisible = DataTab.IsVisible = !narrow;
        OverflowTab.IsVisible = narrow;
        _vm.StackedFields = narrow;
    }

    /// <summary>The overflow button: the tabs the narrow bar has no room for.</summary>
    private async void OnOverflowTabTapped(object? sender, TappedEventArgs e)
    {
        var pick = await Mucka.Core.ActionMenu.ShowAsync(this, OverflowTab, "Settings", "Cancel", null,
            FunctionKeysChoice, NavKeysChoice, DataChoice);
        switch (pick)
        {
            case FunctionKeysChoice:
                _vm.ActiveTab = 2;
                break;
            case NavKeysChoice:
                _vm.ActiveTab = 4;
                break;
            case DataChoice:
                _vm.ActiveTab = 5;
                break;
        }
    }

    // The frame between the Display tab's content and its example block, which draws the content first.
    private const int ExamplesRevealMs = 30;

    private async void OnDisplayTabBuilt(object? sender, EventArgs e)
    {
        await Task.Delay(ExamplesRevealMs);
        _vm.ChatExamplesShown = true;
    }

    private ChatColorPage? _colorPage;

    /// <summary>A chat colour's "...": its own page over this one, with the colour box, sliders, faces and
    /// examples. Its edits are the row's own, taking effect on Apply or Save like the rest.</summary>
    private async void OnColorMoreClicked(object? sender, EventArgs e)
    {
        if (_colorPage is not null || _closed || (sender as Button)?.CommandParameter is not Mucka.ViewModels.ChatColorEditorItem row)
            return;
        var page = new ChatColorPage(row);
        _colorPage = page;
        try
        {
            await Navigation.PushModalAsync(page, Mucka.Core.NavAnimation.ModalAnimated);
            await page.Closed;
        }
        catch (Exception ex)
        {
            Mucka.Core.CrashLog.Write("ChatColor", ex);
        }
        finally
        {
            _colorPage = null;
        }
    }

#if WINDOWS || UIKIT
    /// <summary>Escape: closes the colour page when one is open over this page, else cancels the editor.</summary>
    private void OnEscape()
    {
        if (_colorPage is { } colour)
            _ = colour.CloseAsync();
        else
            _vm.CancelCommand.Execute(null);
    }
#endif

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Subscribed here (not the ctor) to pair with OnDisappearing's unsubscribe: on Android,
        // backgrounding the app fires OnDisappearing without popping the page, and a ctor-only
        // subscription would be lost for good - Cancel/Apply/Save would silently stop closing.
        _vm.CloseRequested -= OnCloseRequested;
        _vm.CloseRequested += OnCloseRequested;
        _vm.SaveFailed -= OnSaveFailed;
        _vm.SaveFailed += OnSaveFailed;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = MacEscape.Attach(this, OnEscape);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.CloseRequested -= OnCloseRequested;
        _vm.SaveFailed -= OnSaveFailed;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = null;
#endif
    }

    private bool _closed;
    private FontSetupPage? _fontSetup;

    /// <summary>
    /// The Text size page over this one. Its choice comes back into this page's own fields, which
    /// take effect on Apply or Save like the rest of them; the live UI step goes back to what it was,
    /// so Cancel here still cancels it.
    /// </summary>
    private async void OnFontSetupClicked(object? sender, EventArgs e)
    {
        if (_fontSetup is not null || _closed) return;
        try
        {
            var live = Mucka.Core.UiFontScaleState.Current.Step;
            var tcs = new TaskCompletionSource<FontSetupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _fontSetup = new FontSetupPage(
                new Mucka.Ui.FontSetupChoice(_vm.UiFontStep, _vm.FontSizeDisplay), live, firstRun: false,
                _vm.ChatPalette, _vm.SelectedChatTheme, tcs);
            await Navigation.PushModalAsync(_fontSetup, Mucka.Core.NavAnimation.ModalAnimated);
            var result = await tcs.Task;
            _fontSetup = null;
            Mucka.Core.UiFontScaleState.Current.Step = live;
            if (result is not null)
            {
                _vm.UiFontStep = result.Sizes.UiStep;
                _vm.FontSize = result.Sizes.GameFontPx;
                if (result.Theme is { } theme)
                    _vm.ApplyChatTheme(theme);
            }
        }
        catch (Exception ex)
        {
            _fontSetup = null;
            Mucka.Core.CrashLog.Write("FontSetup", ex);
        }
    }

    /// <summary>
    /// Closes this page, at most once, and only while it is genuinely the top modal. Both guards
    /// matter because GamePage can close this page out from under the player (being dropped from
    /// the game outranks editing settings) - a bare <c>PopModalAsync()</c> racing that would pop
    /// whatever is on top instead, which is by then the persona-login overlay. A colour page or the
    /// Text size page, when open over this one, is closed first (the Text size page as a Cancel).
    /// </summary>
    public async Task CloseAsync()
    {
        if (_closed)
            return;
        _closed = true;
        if (_colorPage is { } colour)
            await colour.CloseAsync();
        if (_fontSetup is { } fontSetup)
            await fontSetup.CloseAsync(null);

        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync(Mucka.Core.NavAnimation.ModalAnimated);
    }

    private void OnCloseRequested() =>
        MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());

    private void OnSaveFailed(string message) =>
        MainThread.BeginInvokeOnMainThread(async () =>
            await DisplayAlertAsync("Save failed", message, "OK"));

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            OnEscape();
        }
    }
#endif

    private void OnImportClicked(object? sender, EventArgs e)
    {
#if DESKTOP
        _ = ImportAsync();
#endif
    }

#if DESKTOP
    private async Task ImportAsync()
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Select clio.ini",
                FileTypes = new FilePickerFileType(
                    new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, IniFileTypes },
                        { DevicePlatform.MacCatalyst, IniUniformTypes }
                    })
            };
            var result = await FilePicker.Default.PickAsync(options);
            if (result == null) return;

            var fkeys = ParseClioIni(result.FullPath);
            _vm.ImportFkeys(fkeys);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Import failed", ex.Message, "OK");
        }
    }

    /// <summary>
    /// Parses a clio.ini file and returns a 36-element fkeys array.
    /// Keys F1-F12 map to indices 0-11 (None), F13-F24 to 12-23 (Shift),
    /// F25-F36 to 24-35 (Ctrl), matching clio's macros[0..35] layout.
    /// </summary>
    private static string[] ParseClioIni(string path)
    {
        var fkeys = new string[36];
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq < 2) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..];
            if (key.Length < 2 || key.Length > 3) continue;
            if (key[0] != 'F' && key[0] != 'f') continue;
            if (!int.TryParse(key[1..], out var n) || n < 1 || n > 36) continue;
            fkeys[n - 1] = val;
        }
        return fkeys;
    }
#endif
}
