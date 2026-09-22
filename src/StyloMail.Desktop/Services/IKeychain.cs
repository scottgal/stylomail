namespace StyloMail.Desktop.Services;

/// <summary>
/// The platform's secret store.
/// </summary>
/// <remarks>
/// Spec 10.3 is explicit that the console's API key goes in the platform
/// keychain: never a config file, never a log, never a view. The keychain is a
/// platform API, so this is a seam in the same shape as mylo's
/// <c>ISystemNotifier</c>: the app depends on the interface, and the macOS
/// implementation is the one place that reaches outside managed code.
///
/// <para>
/// <b>A key never has a read-back path into the UI.</b> <see cref="Read"/>
/// exists for the request pipeline. Nothing binds to it, nothing renders it,
/// and there is no method here that lists or describes a stored value.
/// </para>
/// </remarks>
public interface IKeychain
{
    /// <summary>The stored secret, or null when there is none.</summary>
    string? Read(string service, string account);

    /// <summary>
    /// Stores a secret, replacing any existing one for the same pair.
    /// </summary>
    /// <remarks>
    /// Replace rather than fail on duplicate. Rotating a key is the ordinary
    /// reason to call this a second time, and a store that refused would leave
    /// the operator to work out that they must delete first.
    /// </remarks>
    void Write(string service, string account, string value);

    /// <summary>Removes a stored secret. Removing one that is not there is not an error.</summary>
    void Delete(string service, string account);
}
