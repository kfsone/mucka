using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class FkeyEditorPage : ContentPage
{
    private readonly FkeyEditorViewModel _vm;

#if WINDOWS
    // The picker's file-type list, built once: every ".ini" import rebuilt the same two strings.
    private static readonly string[] IniFileTypes = [".ini", "*"];
#endif

    /// <summary>The chat examples' font size, as the XAML draws them.</summary>
    public const double ChatExampleFontSize = 14;

    /// <summary>The padding of the pane-coloured box the chat examples sit in.</summary>
    public static readonly Thickness ChatExamplePadding = new(10, 8);

    // Cascadia Mono, upright and italic alike: every glyph advances 1200 of the font's 2048 units per em.
    private const double CascadiaMonoAdvanceEm = 1200.0 / 2048.0;

    public FkeyEditorPage(FkeyEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        ChatRows.SizeChanged += (_, _) => PlaceChatExamples();
#if WINDOWS
        ImportButton.IsVisible = true;
        ImportButton.Clicked += OnImportClickedAsync;
#endif
    }

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
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
        DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.CloseRequested -= OnCloseRequested;
        _vm.SaveFailed -= OnSaveFailed;
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
#if WINDOWS
        ImportButton.Clicked -= OnImportClickedAsync;
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#endif
    }

    // A rotation on Android reconfigures the activity in place (MainActivity's ConfigurationChanges),
    // so re-place the examples off the post-rotation layout as well as off the rows' own resize.
    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
        => Dispatcher.Dispatch(PlaceChatExamples);

    /// <summary>
    /// Puts each chat colour row's examples beside its controls when the row can hold the label
    /// column, the controls at their measured width and the longest example unclipped; otherwise
    /// keeps them in the one block below. The controls come from the layout (the Auto column
    /// measures them unconstrained, in either placement), the examples from their character count
    /// in the monospaced terminal font, so neither side of the test changes with the placement.
    /// </summary>
    private void PlaceChatExamples()
    {
        double available = ChatRows.Width;
        if (available <= 0) return;

        Grid? first = null;
        double controls = 0;
        foreach (var row in ChatRows.Children.OfType<Grid>())
        {
            first ??= row;
            foreach (var stack in row.Children.OfType<HorizontalStackLayout>())
                controls = Math.Max(controls, stack.DesiredSize.Width);
        }
        if (first is null || controls <= 0 || _vm.ChatExamples.Count == 0) return;

        // One spare advance beyond the longest line, so layout rounding or a bold face's ink overhang
        // cannot clip the last glyph of a NoWrap line on a window exactly at the threshold.
        double examples = (_vm.ChatExamples.Max(line => line.Length) + 1) * ChatExampleFontSize * CascadiaMonoAdvanceEm
                        + ChatExamplePadding.HorizontalThickness;
        double needed = first.ColumnDefinitions[0].Width.Value + 2 * first.ColumnSpacing + controls + examples;
        _vm.ChatExamplesBeside = available >= needed;
    }

    private bool _closed;

    /// <summary>
    /// Closes this page, at most once, and only while it is genuinely the top modal. Both guards
    /// matter because GamePage can close this page out from under the player (being dropped from
    /// the game outranks editing settings) - a bare <c>PopModalAsync()</c> racing that would pop
    /// whatever is on top instead, which is by then the persona-login overlay.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_closed)
            return;
        _closed = true;

        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync();
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
            _vm.CancelCommand.Execute(null);
        }
    }

    private async void OnImportClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Select clio.ini",
                FileTypes = new FilePickerFileType(
                    new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, IniFileTypes }
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
