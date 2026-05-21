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
        var window = new Window(new NavigationPage(new ConnectPage())
        {
            BarBackgroundColor = Color.FromArgb("#161b22"),
            BarTextColor = Colors.White,
        });
        window.Title = "mucka";

#if WINDOWS
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