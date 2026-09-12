using Microsoft.UI.Xaml;

namespace Mucka.WinUI;

/// <summary>
/// The WinUI application entry point; wires up logging for unhandled WinUI exceptions.
/// </summary>
public partial class App : MauiWinUIApplication
{
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

