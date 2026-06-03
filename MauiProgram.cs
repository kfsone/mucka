using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mucka.Core;
using Mucka.Pages;
using Mucka.ViewModels;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace Mucka;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                // Registered so MAUI controls (input box, side panel) can resolve it by name.
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
