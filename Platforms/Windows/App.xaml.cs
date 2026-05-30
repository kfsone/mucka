using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mucka.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();
		// Catch any managed exception that reaches the WinUI 3 dispatcher before it
		// triggers RaiseFailFastException (0xc000027b). Log it and keep the app alive
		// so the user sees an error rather than a silent crash.
		this.UnhandledException += OnWinUIUnhandledException;
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	private static void OnWinUIUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
	{
		try
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-crash.txt");
			System.IO.File.AppendAllText(path,
				$"{DateTimeOffset.Now:o}  [WinUI3 UnhandledException]\n{e.Exception}\n\n");
		}
		catch { }
		e.Handled = true; // prevent 0xc000027b process termination
	}
}

