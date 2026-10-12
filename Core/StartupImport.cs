#if WINDOWS
using System.Runtime.InteropServices;
using Mucka.Store;

namespace Mucka.Core;

/// <summary>
/// <c>mucka -import &lt;file&gt;</c>: runs <see cref="DatabaseImport"/> before the app builds, so
/// nothing has opened the database, and reports the result in a message box. Mucka then starts as
/// normal either way.
///
/// <para>The merge is MAUI-free and lives in <c>Mucka.Store</c>; this is only the start-up hook and
/// the box. A native box rather than a page, because there is no window yet.</para>
/// </summary>
internal static class StartupImport
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbIconInformation = 0x40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    internal static void RunIfRequested()
    {
        if (CommandLineArgs.Current.ImportPath is not { } path)
            return;

        var outcome = DatabaseImport.Run(MuckaPaths.GetDatabasePath(), Path.GetFullPath(path));
        _ = MessageBox(IntPtr.Zero, outcome.Message, "Mucka import",
            MbOk | (outcome.Ok ? MbIconInformation : MbIconError));
    }
}
#endif
