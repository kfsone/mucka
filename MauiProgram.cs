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
#if WINDOWS
        // First thing, before anything can open the database.
        StartupImport.RunIfRequested();
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                // Registered so MAUI controls (input box, side panel) can resolve it by name.
                fonts.AddFont("CascadiaMono.ttf", "Cascadia Mono");
                // The terminal's italic face, for controls that preview what the pane draws.
                fonts.AddFont("CascadiaMonoItalic.ttf", "Cascadia Mono Italic");
            });

#if MACCATALYST
        // Skia's native canvas view is opaque by default on a Mac, so a canvas that draws on a
        // transparent clear (the Combat Rail, the compass) shows a solid white box. Marked
        // non-opaque, and left clear unless the element asks for a background of its own.
        SkiaSharp.Views.Maui.Handlers.SKCanvasViewHandler.SKCanvasViewMapper.AppendToMapping("MuckaNonOpaque", (handler, view) =>
        {
            if (handler.PlatformView is not UIKit.UIView native)
                return;
            native.Opaque = false;
            if (view is not VisualElement { BackgroundColor.Alpha: > 0 })
                native.BackgroundColor = UIKit.UIColor.Clear;
        });
#endif

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
