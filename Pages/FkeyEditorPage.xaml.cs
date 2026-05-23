using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class FkeyEditorPage : ContentPage
{
    private readonly FkeyEditorViewModel _vm;

    public FkeyEditorPage(FkeyEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.CloseRequested += OnCloseRequested;
#if WINDOWS
        ImportButton.IsVisible = true;
        ImportButton.Clicked += OnImportClickedAsync;
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
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
#if WINDOWS
        ImportButton.Clicked -= OnImportClickedAsync;
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#endif
    }

    private void OnCloseRequested() =>
        MainThread.BeginInvokeOnMainThread(async () => await Navigation.PopModalAsync());

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
                        { DevicePlatform.WinUI, new[] { ".ini", "*" } }
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
