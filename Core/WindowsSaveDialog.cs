#if WINDOWS
using System.Runtime.InteropServices;

namespace Mucka.Core;

/// <summary>
/// The Windows save dialog (the shell's <c>IFileSaveDialog</c>), opened on a folder of our choosing.
/// WinRT's <c>FileSavePicker</c> can only start at a fixed place (Documents, Desktop and the like),
/// and the Windows App SDK picker that takes a folder has no C# projection in the SDK this app
/// builds against.
///
/// <para>Modal on its owner window, and synchronous: call it on the UI thread, which is the STA the
/// shell's dialog requires. The dialog pumps the window's messages while it is open.</para>
/// </summary>
internal static class WindowsSaveDialog
{
    /// <summary>Shows the dialog; returns the chosen path, or null when the player cancelled.</summary>
    /// <param name="owner">The window the dialog is modal on.</param>
    /// <param name="folder">Where it opens; when it does not exist, the dialog's own choice.</param>
    /// <param name="fileName">The name it offers, without the extension.</param>
    /// <param name="typeName">The file type's name in the type list ("Text log").</param>
    /// <param name="extension">The file type's extension, with its dot (".txt").</param>
    public static string? Show(IntPtr owner, string folder, string fileName, string typeName, string extension)
    {
        var dialog = (IFileDialog)new FileSaveDialog();
        try
        {
            dialog.SetFileTypes(1, [new FilterSpec { Name = typeName, Spec = "*" + extension }]);
            dialog.SetDefaultExtension(extension.TrimStart('.'));
            dialog.SetFileName(fileName);
            if (Directory.Exists(folder) && FolderItem(folder) is { } item)
            {
                try
                {
                    // SetFolder, not SetDefaultFolder: the folder is where it opens every time, not
                    // only until the dialog remembers a folder of its own.
                    dialog.SetFolder(item);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | OverwritePrompt | ForceFileSystem | PathMustExist | NoChangeDir);

            var shown = dialog.Show(owner);
            if (shown == Cancelled)
                return null;
            Marshal.ThrowExceptionForHR(shown);

            dialog.GetResult(out var result);
            try
            {
                result.GetDisplayName(FileSystemPath, out var name);
                try
                {
                    return Marshal.PtrToStringUni(name);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>The shell's item for <paramref name="folder"/>, or null when it has none: the dialog
    /// then opens where it would have anyway, and the save goes on.</summary>
    private static IShellItem? FolderItem(string folder)
    {
        var shellItem = typeof(IShellItem).GUID;
        try
        {
            SHCreateItemFromParsingName(folder, IntPtr.Zero, ref shellItem, out var item);
            return item;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // FILEOPENDIALOGOPTIONS
    private const uint OverwritePrompt = 0x2;
    private const uint NoChangeDir = 0x8;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;

    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): what Show returns when the player cancels.</summary>
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>SIGDN_FILESYSPATH.</summary>
    private const uint FileSystemPath = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        out IShellItem item);

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialog;

    /// <summary>COMDLG_FILTERSPEC.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    /// <summary>IFileDialog, its IModalWindow::Show first: the vtable order, every method declared
    /// whether used or not.</summary>
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    /// <summary>IShellItem, in vtable order.</summary>
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
#endif
