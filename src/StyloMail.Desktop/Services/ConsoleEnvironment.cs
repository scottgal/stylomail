namespace StyloMail.Desktop.Services;

/// <summary>
/// Where the console points, and which keychain it uses.
/// </summary>
/// <remarks>
/// Both answers are ordinary configuration in a Release build. The overrides
/// below exist only in a Debug build and only for the verification harnesses, in
/// the same spirit as mylo's <c>MYLO_DATA_DIR</c>: a screenshot or a smoke run
/// needs a known Host and a key it put there itself, and it must not be pointed
/// at whatever the person running it happens to have configured.
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
    /// is the keychain, entered once through the connection screen and never
    /// shown again. An environment variable is a config file that leaks through
    /// a process listing, which is exactly what spec 10.3 rules out, so this is
    /// compiled out of a Release build rather than merely discouraged.
    /// </remarks>
    public const string KeyVariable = "STYLOMAIL_SMOKE_KEY";

    private const string DefaultHost = "http://127.0.0.1:5000";

    /// <summary>
    /// Whether a harness is driving this run.
    /// </summary>
    /// <remarks>
    /// When it is, the console keeps its settings in memory rather than on
    /// disk. A harness run must not read a developer's chosen Host, and must
    /// not leave one behind for them either.
    /// </remarks>
    private static bool IsHarnessRun =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostVariable))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable));

    /// <summary>The console's own settings: a host address, and nothing secret.</summary>
    public static IConsoleSettings Settings()
    {
#if DEBUG
        if (IsHarnessRun) return new InMemoryConsoleSettings();
#endif

        return JsonConsoleSettings.Load(DefaultSettingsPath());
    }

    /// <summary>Where the settings file lives, under the platform's application data.</summary>
    public static string DefaultSettingsPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StyloMail",
            "console.json");

    /// <summary>
    /// The address to use, in precedence order: a harness override, then what
    /// the operator chose, then the default.
    /// </summary>
    public static Uri HostAddress(IConsoleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

#if DEBUG
        var overridden = Environment.GetEnvironmentVariable(HostVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && Uri.TryCreate(overridden, UriKind.Absolute, out var harness))
        {
            // Not validated against HostAddressPolicy on purpose: a harness
            // run's Host is on loopback by construction, and a Debug-only
            // override that refused to start would make a scripted run fail
            // somewhere less obvious than here.
            return harness;
        }
#endif

        if (!string.IsNullOrWhiteSpace(settings.HostAddress)
            && Uri.TryCreate(settings.HostAddress, UriKind.Absolute, out var stored))
        {
            return stored;
        }

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
