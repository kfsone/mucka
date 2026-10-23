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

    // The window opens at once, on a blank page; the profile page replaces it when StartupSequence
    // is done - docs/font-setup-design.md, "The window".
    protected override Window CreateWindow(IActivationState? activationState)
    {
        SoundService.Play("mucka_theme.wav");

#if DESKTOP
        var importPath = Core.CommandLineArgs.Current.ImportPath;
#else
        string? importPath = null;
#endif
        // One NavigationPage for the window's whole life: the profile page replaces the start-up page
        // inside it.
        var startupPage = new StartupPage(importPath);
        var nav = new NavigationPage(startupPage)
        {
            BarBackgroundColor = Color.FromArgb("#161b22"),
            BarTextColor = Colors.White,
        };
        var window = new Window(nav) { Title = LaunchTitle };
        _ = ShowProfilePageAsync(nav, startupPage);

#if DESKTOP
        // Default-size the window at launch - WinUI's default is enormous. Sized for the game
        // view (82 columns + expanded side panel) at the default font size; GamePage re-applies
        // this with the profile's actual font size on first appearance.
        window.Width = Pages.GamePage.PreferredWindowWidthDp(
            ViewModels.GameViewModel.DefaultFontSizePx * ViewModels.GameViewModel.CharWidthPerFontPx,
            panelExpanded: true);
#endif
#if WINDOWS
        window.HandlerChanged += (s, e) => SetWindowIcon(window);
        // Observed: Window.Title reaches the native window (the taskbar shows it) but MAUI's default
        // title bar drew no text, so the window looked anonymous. This one shows Window.Title, live.
        var titleBar = new TitleBar { ForegroundColor = Colors.White, BackgroundColor = Color.FromArgb("#161b22") };
        titleBar.SetBinding(TitleBar.TitleProperty, new Binding(nameof(Window.Title), source: window));
        window.TitleBar = titleBar;
#endif

        // Closing the main window tears the app down -- take the auxiliary windows ($map,
        // $con) with it. Otherwise they are orphaned and keep the process alive invisibly
        // (easy to close Mucka and leave one behind).
        window.Destroying += (_, _) =>
        {
            if (Application.Current is not { } app) return;
            foreach (var other in app.Windows.Where(w => !ReferenceEquals(w, window)).ToList())
                app.CloseWindow(other);
        };

        return window;
    }

    // Profile-mode launches title the window with the profile name so a specific instance is easy
    // to spot in the taskbar / process list; otherwise app + version.
    private static string LaunchTitle => Core.CommandLineArgs.Current.Profile is { Length: > 0 } profile
        ? profile
        : $"mucka {AppInfo.VersionString}";

    private static async Task ShowProfilePageAsync(NavigationPage nav, Page startupPage)
    {
        Core.StartupSequence.Result? startup = null;
        try
        {
            startup = await Core.StartupSequence.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The profile page still opens, at the step the app started with.
            Core.CrashLog.Write("Startup", ex);
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (startup is not null)
                    Core.UiFontScaleState.Current.Step = startup.Font.UiStep;
                var connectPage = IPlatformApplication.Current!.Services.GetRequiredService<ConnectPage>();
                connectPage.Startup = startup;
                nav.Navigation.InsertPageBefore(connectPage, startupPage);
                await nav.PopToRootAsync(animated: false);
            });
        }
        catch (Exception ex)
        {
            Core.CrashLog.Write("Startup", ex);
        }
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