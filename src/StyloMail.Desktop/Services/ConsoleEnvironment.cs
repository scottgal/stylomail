namespace StyloMail.Desktop.Services;

/// <summary>
/// Where the console points, and which keychain it uses.
/// </summary>
/// <remarks>
/// Both answers are constants in a Release build. The overrides below exist
/// only in a Debug build and only for the verification harnesses, in the same
/// spirit as mylo's <c>MYLO_DATA_DIR</c>: a screenshot or a smoke run needs a
/// known Host and a key it put there itself, and it must not be pointed at
/// whatever the person running it happens to have configured.
/// </remarks>
public static class ConsoleEnvironment
{
    /// <summary>The variable naming the Host. Debug only.</summary>
    public const string HostVariable = "STYLOMAIL_HOST";

    /// <summary>
    /// The key for a harness run. Debug only, and deliberately the same name the
    /// live tests read.
    /// </summary>
    /// <remarks>
    /// <b>This is not how an operator configures a key.</b> The production path
    /// is the keychain, entered once through the app and never shown again. An
    /// environment variable is a config file that leaks through a process
    /// listing, which is exactly what spec 10.3 rules out, so this is compiled
    /// out of a Release build rather than merely discouraged.
    /// </remarks>
    public const string KeyVariable = "STYLOMAIL_SMOKE_KEY";

    private const string DefaultHost = "http://127.0.0.1:5000";

    public static Uri HostAddress()
    {
#if DEBUG
        var configured = Environment.GetEnvironmentVariable(HostVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri))
        {
            return uri;
        }
#endif
        return new Uri(DefaultHost);
    }

    public static IKeychain Keychain()
    {
#if DEBUG
        // A harness run supplies its own key, so the real keychain is left
        // alone. Writing a throwaway key into a developer's keychain would
        // overwrite whatever they had there, which is a small thing that is
        // annoying out of all proportion to its size.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            var memory = new InMemoryKeychain();
            new KeychainApiKeyProvider(memory).Store(Environment.GetEnvironmentVariable(KeyVariable)!);
            return memory;
        }
#endif
        return new MacKeychain();
    }
}
