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
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf",  "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
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
