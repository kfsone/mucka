using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mucka.Core;
using Mucka.Pages;
using Mucka.ViewModels;

namespace Mucka;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // WebView2's default user data folder sits next to the exe, which is read-only when
        // installed to Program Files. Point it at a writable per-user location instead.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Mucka", "WebView2"));
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                // Registering as an embedded font prevents MAUI's Windows FontManager from
                // treating the name as a file path (FindFontFamilyName with a relative URI),
                // which would throw InvalidOperationException and corrupt the handler context,
                // causing a downstream NullReferenceException in WebView2Proxy.OnCoreWebView2Initialized.
                fonts.AddFont("CascadiaMono.ttf", "Cascadia Mono");
            });

        // Register ViewModels and Pages for dependency injection.
        // ConnectViewModel is transient so each navigation creates a fresh instance.
        builder.Services.AddTransient<ConnectViewModel>();
        builder.Services.AddTransient<ConnectPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

#if WINDOWS
        if (CommandLineArgs.Current.LogPath is { } logPath)
        {
            var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Trace.Listeners.Add(new TextWriterTraceListener(writer, "mucka-file-log"));
            Trace.AutoFlush = true;
        }
#endif

        return builder.Build();
    }
}
