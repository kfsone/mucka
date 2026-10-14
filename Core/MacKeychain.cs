#if MACCATALYST
using System.Runtime.InteropServices;
using System.Text;

namespace Mucka.Core;

/// <summary>
/// Generic-password storage in the user's login keychain, through the file-based SecKeychain calls.
///
/// <para>Observed: from an ad-hoc signed, entitlement-free Mac Catalyst build, <c>SecKeyChain.Add</c>
/// (and therefore MAUI's <c>SecureStorage</c>) returns <c>MissingEntitlement</c> with
/// <c>UseDataProtectionKeychain</c> unset, false and true. The same build's
/// <c>SecKeychainAddGenericPassword</c>/<c>SecKeychainFindGenericPassword</c>/<c>SecKeychainItemDelete</c>
/// succeed with no prompt.</para>
/// </summary>
internal static class MacKeychain
{
    private const string Service = "org.kfs.mucka";
    private const int ErrSecItemNotFound = -25300;
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, uint passwordLen, byte[] password, out IntPtr item);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, out uint passwordLen, out IntPtr password, out IntPtr item);

    // As the find above, asking for the item only: no password is read, so deleting never needs the
    // keychain's permission to hand the secret over.
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindItem(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, IntPtr passwordLen, IntPtr password, out IntPtr item);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr item);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    public static string? Get(string account)
    {
        var (svc, acct) = Names(account);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)svc.Length, svc, (uint)acct.Length, acct,
            out var len, out var data, out var item);
        if (rc == ErrSecItemNotFound) return null;
        Check(rc, "read");
        try { return Marshal.PtrToStringUTF8(data, (int)len); }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            CFRelease(item);
        }
    }

    // Find then add or update is two calls; two overlapping saves would otherwise meet in the middle.
    private static readonly object s_gate = new();

    public static void Set(string account, string password)
    {
        lock (s_gate)
        {
            var (svc, acct) = Names(account);
            var pw = Encoding.UTF8.GetBytes(password);

            // Updated where it is when it exists, so a failure leaves the old password in place.
            var rc = SecKeychainFindItem(IntPtr.Zero, (uint)svc.Length, svc, (uint)acct.Length, acct,
                IntPtr.Zero, IntPtr.Zero, out var existing);
            if (rc == 0)
            {
                try { Check(SecKeychainItemModifyAttributesAndData(existing, IntPtr.Zero, (uint)pw.Length, pw), "update"); }
                finally { CFRelease(existing); }
                return;
            }
            if (rc != ErrSecItemNotFound)
                Check(rc, "find");

            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)svc.Length, svc, (uint)acct.Length, acct,
                (uint)pw.Length, pw, out var item), "save");
            CFRelease(item);
        }
    }

    public static void Remove(string account)
    {
        lock (s_gate)
            RemoveLocked(account);
    }

    private static void RemoveLocked(string account)
    {
        var (svc, acct) = Names(account);
        var rc = SecKeychainFindItem(IntPtr.Zero, (uint)svc.Length, svc, (uint)acct.Length, acct,
            IntPtr.Zero, IntPtr.Zero, out var item);
        if (rc == ErrSecItemNotFound) return;
        Check(rc, "find");
        try { Check(SecKeychainItemDelete(item), "delete"); }
        finally { CFRelease(item); }
    }

    private static (byte[] Service, byte[] Account) Names(string account)
        => (Encoding.UTF8.GetBytes(Service), Encoding.UTF8.GetBytes(account));

    private static void Check(int rc, string what)
    {
        if (rc != 0)
            throw new InvalidOperationException($"The macOS Keychain could not {what} the password (OSStatus {rc}).");
    }
}
#endif
