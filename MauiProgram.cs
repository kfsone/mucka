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
        // -import runs later, behind the open window: see StartupSequence.
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

#if UIKIT
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
#if IOS
        // Every text field here holds a command, a host, an account or text sent to the game as typed:
        // no predictions, corrections, capitals or smart punctuation, and a keyboard drawn dark like
        // the pages. The assistant bar is emptied: with a hardware keyboard the iPad simulator showed a
        // light strip of keyboard and dictation buttons across the bottom of the screen.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("MuckaTextInput", (handler, _) =>
        {
            var tf = handler.PlatformView;
            tf.KeyboardAppearance = UIKit.UIKeyboardAppearance.Dark;
            tf.AutocorrectionType = UIKit.UITextAutocorrectionType.No;
            tf.AutocapitalizationType = UIKit.UITextAutocapitalizationType.None;
            tf.SpellCheckingType = UIKit.UITextSpellCheckingType.No;
            tf.SmartQuotesType = UIKit.UITextSmartQuotesType.No;
            tf.SmartDashesType = UIKit.UITextSmartDashesType.No;
            if (OperatingSystem.IsIOSVersionAtLeast(17))
                tf.InlinePredictionType = UIKit.UITextInlinePredictionType.No;
            tf.InputAssistantItem.LeadingBarButtonGroups = [];
            tf.InputAssistantItem.TrailingBarButtonGroups = [];
        });
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("MuckaTextInput", (handler, _) =>
        {
            var tv = handler.PlatformView;
            tv.KeyboardAppearance = UIKit.UIKeyboardAppearance.Dark;
            tv.AutocorrectionType = UIKit.UITextAutocorrectionType.No;
            tv.AutocapitalizationType = UIKit.UITextAutocapitalizationType.None;
            tv.SpellCheckingType = UIKit.UITextSpellCheckingType.No;
            tv.SmartQuotesType = UIKit.UITextSmartQuotesType.No;
            tv.SmartDashesType = UIKit.UITextSmartDashesType.No;
            if (OperatingSystem.IsIOSVersionAtLeast(17))
                tv.InlinePredictionType = UIKit.UITextInlinePredictionType.No;
            tv.InputAssistantItem.LeadingBarButtonGroups = [];
            tv.InputAssistantItem.TrailingBarButtonGroups = [];
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
