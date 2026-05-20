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
        window.Title = $"mucka {AppInfo.VersionString}";
        return window;
    }
}