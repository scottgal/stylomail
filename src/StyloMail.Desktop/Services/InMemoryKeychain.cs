using System.Collections.Concurrent;

namespace StyloMail.Desktop.Services;

/// <summary>
/// A keychain that keeps nothing.
/// </summary>
/// <remarks>
/// <b>Not a production store, and the name says so.</b> It exists for the two
/// cases where the platform keychain is the wrong tool: a headless screenshot
/// run, which must not write into a developer's real keychain, and a test,
/// which must not depend on a login session. A process using this loses the key
/// on exit, which is a worse experience and not a weaker guarantee.
///
/// <para>
/// The production path is <see cref="MacKeychain"/> behind
/// <see cref="KeychainApiKeyProvider"/>. This is never selected by a Release
/// build.
/// </para>
/// </remarks>
public sealed class InMemoryKeychain : IKeychain
{
    private readonly ConcurrentDictionary<(string Service, string Account), string> _items = new();

    public string? Read(string service, string account)
        => _items.TryGetValue((service, account), out var value) ? value : null;

    public void Write(string service, string account, string value)
        => _items[(service, account)] = value;

    public void Delete(string service, string account)
        => _items.TryRemove((service, account), out _);
}
