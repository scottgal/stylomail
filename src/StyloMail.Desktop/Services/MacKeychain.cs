using System.Runtime.InteropServices;
using System.Text;

namespace StyloMail.Desktop.Services;

/// <summary>
/// The macOS keychain, reached through Security.framework and CoreFoundation
/// directly.
/// </summary>
/// <remarks>
/// <b>Why this shape rather than any of the alternatives.</b> Spawning
/// <c>/usr/bin/security</c> would be shorter, and it is rejected on the same
/// grounds mylo rejects <c>osascript</c> for notifications: it puts the secret
/// on a command line, where every other process on the machine can read it out
/// of the process table. A third-party keychain package would be a dependency
/// shipped for four functions. So: P/Invokes into the two system frameworks,
/// no package, and nothing added to the build but this file.
///
/// <para>
/// <b>The value crosses into the keychain and comes back as bytes.</b> Nothing
/// here logs, formats or describes a secret. Every failure path returns null or
/// does nothing quietly, because an exception message describing a keychain
/// failure is one refactor away from carrying the key.
/// </para>
///
/// <para>
/// <b>Availability is a real question rather than a formality.</b> On a
/// non-macOS host, or in a process with no logged-in keychain session, the
/// framework loads but no item is ever found. <see cref="IsAvailable"/> reports
/// the platform, and the caller treats an unavailable keychain as "no key
/// configured" rather than as an error, which is the same state an operator
/// meets on first run.
/// </para>
/// </remarks>
public sealed class MacKeychain : IKeychain
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";

    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>UTF-8. The full enumeration value, not the one CFStringGetSystemEncoding would give.</summary>
    private const uint EncodingUtf8 = 0x08000100;

    private const int ErrSecSuccess = 0;

    private readonly bool _available;

    public MacKeychain()
    {
        _available = OperatingSystem.IsMacOS() && Probe();
    }

    public bool IsAvailable => _available;

    public string? Read(string service, string account)
    {
        if (!_available) return null;

        var classKey = Constant("kSecClass");
        var serviceKey = Constant("kSecAttrService");
        var accountKey = Constant("kSecAttrAccount");
        var returnDataKey = Constant("kSecReturnData");
        var matchLimitKey = Constant("kSecMatchLimit");
        var matchLimitOne = Constant("kSecMatchLimitOne");
        var genericPassword = Constant("kSecClassGenericPassword");

        if (classKey == IntPtr.Zero || genericPassword == IntPtr.Zero) return null;

        var serviceValue = CfString(service);
        var accountValue = CfString(account);

        var keys = new[] { classKey, serviceKey, accountKey, returnDataKey, matchLimitKey };
        var values = new[] { genericPassword, serviceValue, accountValue, CfTrue(), matchLimitOne };

        var query = CfDictionary(keys, values);

        Release(serviceValue, accountValue);
        if (query == IntPtr.Zero) return null;

        try
        {
            var status = SecItemCopyMatching(query, out var result);

            if (status != ErrSecSuccess || result == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return ReadData(result);
            }
            finally
            {
                Release(result);
            }
        }
        catch (Exception)
        {
            // A keychain read is never worth taking the app down for, and this
            // is one of the few places reaching outside managed code.
            return null;
        }
        finally
        {
            Release(query);
        }
    }

    public void Write(string service, string account, string value)
    {
        if (!_available) return;

        // Delete first rather than using SecItemUpdate or returning a duplicate
        // error. Rotating a key is the ordinary reason to call this twice, and
        // delete-then-add is the one path that cannot leave the store in the
        // state where an old key is still the one that answers.
        Delete(service, account);

        var classKey = Constant("kSecClass");
        var serviceKey = Constant("kSecAttrService");
        var accountKey = Constant("kSecAttrAccount");
        var valueKey = Constant("kSecValueData");
        var genericPassword = Constant("kSecClassGenericPassword");

        if (classKey == IntPtr.Zero || genericPassword == IntPtr.Zero) return;

        var serviceValue = CfString(service);
        var accountValue = CfString(account);
        var dataValue = CfData(value);

        var keys = new[] { classKey, serviceKey, accountKey, valueKey };
        var values = new[] { genericPassword, serviceValue, accountValue, dataValue };

        var attributes = CfDictionary(keys, values);

        Release(serviceValue, accountValue, dataValue);
        if (attributes == IntPtr.Zero) return;

        try
        {
            _ = SecItemAdd(attributes, IntPtr.Zero);
        }
        catch (Exception)
        {
            // As above: a failure to store is reported by the next read finding
            // nothing, which is a state the console already renders.
        }
        finally
        {
            Release(attributes);
        }
    }

    public void Delete(string service, string account)
    {
        if (!_available) return;

        var classKey = Constant("kSecClass");
        var serviceKey = Constant("kSecAttrService");
        var accountKey = Constant("kSecAttrAccount");
        var genericPassword = Constant("kSecClassGenericPassword");

        if (classKey == IntPtr.Zero || genericPassword == IntPtr.Zero) return;

        var serviceValue = CfString(service);
        var accountValue = CfString(account);

        var keys = new[] { classKey, serviceKey, accountKey };
        var values = new[] { genericPassword, serviceValue, accountValue };

        var query = CfDictionary(keys, values);

        Release(serviceValue, accountValue);
        if (query == IntPtr.Zero) return;

        try
        {
            // errSecItemNotFound is the expected answer when there was nothing
            // to remove, and is not a failure.
            _ = SecItemDelete(query);
        }
        catch (Exception)
        {
            // As above.
        }
        finally
        {
            Release(query);
        }
    }

    private static string? ReadData(IntPtr data)
    {
        if (data == IntPtr.Zero) return null;

        var length = (int)CFDataGetLength(data);
        if (length <= 0) return null;

        var bytes = CFDataGetBytePtr(data);
        if (bytes == IntPtr.Zero) return null;

        var buffer = new byte[length];
        Marshal.Copy(bytes, buffer, 0, length);

        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>
    /// True when the frameworks load and the keychain classes resolve.
    /// </summary>
    /// <remarks>
    /// A bundle test would be wrong here, unlike for notifications. A console
    /// run from bin/ has no bundle identifier, but its keychain access still
    /// works: the item is scoped to the login session, not to a bundle.
    /// </remarks>
    private static bool Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad(SecurityFramework, out _)) return false;
            if (!NativeLibrary.TryLoad(CoreFoundationFramework, out _)) return false;

            return Constant("kSecClass") != IntPtr.Zero
                && Constant("kSecClassGenericPassword") != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IntPtr _securityHandle;
    private static IntPtr _coreFoundationHandle;

    /// <summary>
    /// Reads one of Security.framework's exported CFStringRef constants.
    /// </summary>
    /// <remarks>
    /// The symbol is a global variable holding a <c>CFStringRef</c>, so the
    /// export address has to be dereferenced once. Using the address itself as
    /// the string would pass a pointer to a pointer, which the framework
    /// rejects with an unhelpful error rather than crashing, which is the sort
    /// of bug that takes an afternoon.
    /// </remarks>
    private static IntPtr Constant(string name)
    {
        try
        {
            if (_securityHandle == IntPtr.Zero
                && !NativeLibrary.TryLoad(SecurityFramework, out _securityHandle))
            {
                return IntPtr.Zero;
            }

            return Marshal.ReadIntPtr(NativeLibrary.GetExport(_securityHandle, name));
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr CfTypeDictionaryCallbacks(string name)
    {
        try
        {
            if (_coreFoundationHandle == IntPtr.Zero
                && !NativeLibrary.TryLoad(CoreFoundationFramework, out _coreFoundationHandle))
            {
                return IntPtr.Zero;
            }

            return NativeLibrary.GetExport(_coreFoundationHandle, name);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr CfString(string value)
    {
        try
        {
            EnsureCoreFoundation();
            return CFStringCreateWithCString(IntPtr.Zero, value, EncodingUtf8);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr CfData(string value)
    {
        try
        {
            EnsureCoreFoundation();

            var bytes = Encoding.UTF8.GetBytes(value);
            return CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>The CFBoolean true, obtained rather than constructed.</summary>
    private static IntPtr CfTrue() => Constant("kCFBooleanTrue");

    private static IntPtr CfDictionary(IntPtr[] keys, IntPtr[] values)
    {
        try
        {
            EnsureCoreFoundation();

            var keyCallbacks = CfTypeDictionaryCallbacks("kCFTypeDictionaryKeyCallBacks");
            var valueCallbacks = CfTypeDictionaryCallbacks("kCFTypeDictionaryValueCallBacks");

            if (keyCallbacks == IntPtr.Zero || valueCallbacks == IntPtr.Zero) return IntPtr.Zero;

            return CFDictionaryCreate(
                IntPtr.Zero, keys, values, keys.Length, keyCallbacks, valueCallbacks);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static void EnsureCoreFoundation()
    {
        if (_coreFoundationHandle == IntPtr.Zero)
        {
            NativeLibrary.TryLoad(CoreFoundationFramework, out _coreFoundationHandle);
        }
    }

    private static void Release(params IntPtr[] references)
    {
        foreach (var reference in references)
        {
            if (reference == IntPtr.Zero) continue;

            try
            {
                CFRelease(reference);
            }
            catch (Exception)
            {
                // Releasing is best effort by definition.
            }
        }
    }

    [DllImport(SecurityFramework)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityFramework)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator,
        IntPtr[] keys,
        IntPtr[] values,
        nint count,
        IntPtr keyCallbacks,
        IntPtr valueCallbacks);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr reference);
}
