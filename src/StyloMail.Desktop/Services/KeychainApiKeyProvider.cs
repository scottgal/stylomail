using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Services;

/// <summary>
/// Reads the console's API key from the platform keychain.
/// </summary>
/// <remarks>
/// <b>The only production implementation of <see cref="IApiKeyProvider"/>.</b>
/// Spec 10.3 says the key is entered once and stored in the keychain, never in
/// a config file, never in a log, never in a view. An environment variable is a
/// config file that leaks through a process listing, which is why the smoke
/// harness in the test project keeps its provider there rather than here.
/// </remarks>
public sealed class KeychainApiKeyProvider : IApiKeyProvider
{
    /// <summary>Keychain service name. Visible in Keychain Access, so it says what it is for.</summary>
    public const string ServiceName = "StyloMail Operator Console";

    /// <summary>
    /// The single account this console holds.
    /// </summary>
    /// <remarks>
    /// One key, not one per Host. The console talks to one Host: a per-Host
    /// account would imply the app holds several credentials and knew how to
    /// choose between them, which it does not.
    /// </remarks>
    public const string KeyAccount = "host-api-key";

    private readonly IKeychain _keychain;

    public KeychainApiKeyProvider(IKeychain keychain)
    {
        ArgumentNullException.ThrowIfNull(keychain);
        _keychain = keychain;
    }

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var key = _keychain.Read(ServiceName, KeyAccount);

        // Empty and whitespace are the same answer as absent: no key is
        // configured. Passing a blank through would put an empty header on the
        // wire and turn a configuration gap into a puzzling 401.
        return ValueTask.FromResult(
            string.IsNullOrWhiteSpace(key) ? null : key);
    }

    /// <summary>Stores a key, replacing any existing one. Called once, from the key entry.</summary>
    public void Store(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _keychain.Write(ServiceName, KeyAccount, key);
    }

    /// <summary>Removes the stored key, for signing out or rotating.</summary>
    public void Clear() => _keychain.Delete(ServiceName, KeyAccount);

    /// <summary>Whether a key is stored, without reading it.</summary>
    /// <remarks>
    /// The UI needs to know whether to show "connect" or "reconnect", and it
    /// must be able to answer that without a code path that holds the value.
    /// </remarks>
    public bool HasKey() => !string.IsNullOrWhiteSpace(_keychain.Read(ServiceName, KeyAccount));
}
