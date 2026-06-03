using Microsoft.Extensions.DependencyInjection;
using Mucka.Audio;
using Mucka.Pages;

namespace Mucka;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

    protected override Window CreateWindow(IActivationState? activationState)
    {
        SoundService.Play("mucka_theme.wav");

        var connectPage = IPlatformApplication.Current!.Services.GetRequiredService<ConnectPage>();
        var window = new Window(new NavigationPage(connectPage)
        {
            BarBackgroundColor = Color.FromArgb("#161b22"),
            BarTextColor = Colors.White,
        });
        // Profile-mode launches title the window with the profile name so a specific
        // instance is easy to spot in the taskbar / process list; otherwise app + version.
        window.Title = Core.CommandLineArgs.Current.Profile is { Length: > 0 } profile
            ? profile
            : $"mucka {AppInfo.VersionString}";

#if WINDOWS
        // Default-size the window at launch — WinUI's default is enormous. Sized for the game
        // view (82 columns + expanded side panel) at the default font size; GamePage re-applies
        // this with the profile's actual font size on first appearance.
        window.Width = Pages.GamePage.PreferredWindowWidthDp(
            ViewModels.GameViewModel.DefaultFontSizePx * ViewModels.GameViewModel.CharWidthPerFontPx,
            panelExpanded: true);

        window.HandlerChanged += (s, e) => SetWindowIcon(window);
#endif

        return window;
    }

#if WINDOWS
    static void SetWindowIcon(Window window)
    {
        var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "muckabase.ico");
        if (System.IO.File.Exists(icoPath))
            nativeWindow.AppWindow.SetIcon(icoPath);
    }
#endif
}