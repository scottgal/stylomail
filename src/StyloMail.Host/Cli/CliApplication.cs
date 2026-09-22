using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloMail.Host.Auth;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Cli;

/// <summary>
/// Argument parsing and dispatch for the standalone executable.
/// </summary>
/// <remarks>
/// <c>serve</c> is the default command, but an <em>unrecognised</em> command is an error rather
/// than a fall-through. A typo silently starting a network listener would be a surprising thing for
/// a mail component to do.
/// </remarks>
public static class CliApplication
{
    public const string Usage =
        """
        StyloMail host

          serve                                        Run the HTTP host (default)
          assess <file.eml> [--tenant <id>] [--semantic] [--json]
                                                       Assess one message. Local only unless
                                                       --semantic is given.
          replay <directory> [--tenant <id>] [--json]  Deterministically re-analyse fixtures
          quarantine list --tenant <id> [--json]
          quarantine release <queue-id> --tenant <id> --by <principal>
          profiles inspect --tenant <id> [--json]
          key create --principal <id> --tenant <id> --privileges <a,b[,c]> --by <principal>
                     [--sender <identity>]…
                                                       Mint an API key. The value is printed once.
          key list [--json]                            Minted and configured principals, with source
          key revoke --principal <id> --by <principal> Revoke a minted key. Refuses on a
                                                       configuration principal.
        """;

    /// <summary>Parses arguments. Returns false with an error message when they are not understood.</summary>
    public static bool TryParse(string[] args, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;

        // Anything that starts with an option is host configuration, not a command. The web host
        // is started by the tooling with its own arguments (`--urls`, `--contentRoot`, …), and
        // treating the first of those as a command name would refuse to start the server.
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            command = new ServeCommand();
            return true;
        }

        var head = args[0];
        var rest = args[1..];

        switch (head.ToLowerInvariant())
        {
            case "serve":
                command = new ServeCommand();
                return true;

            case "assess":
                if (!TryPositional(rest, out var path, out error))
                {
                    return false;
                }

                command = new AssessCommand(path, Flag(rest, "--tenant"), rest.Contains("--semantic"), rest.Contains("--json"));
                return true;

            case "replay":
                if (!TryPositional(rest, out var directory, out error))
                {
                    return false;
                }

                command = new ReplayCommand(directory, Flag(rest, "--tenant"), rest.Contains("--json"));
                return true;

            case "quarantine":
                return TryParseQuarantine(rest, out command, out error);

            case "profiles":
                if (rest.Length == 0 || !string.Equals(rest[0], "inspect", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Usage: profiles inspect --tenant <id>";
                    return false;
                }

                if (Flag(rest, "--tenant") is not { Length: > 0 } profilesTenant)
                {
                    error = "profiles inspect requires --tenant <id>: a profile listing is never unscoped.";
                    return false;
                }

                command = new ProfilesInspectCommand(profilesTenant, rest.Contains("--json"));
                return true;

            case "key":
                return TryParseKey(rest, out command, out error);

            default:
                error = $"Unknown command '{head}'.\n\n{Usage}";
                return false;
        }
    }

    /// <summary>
    /// Parses the three <c>key</c> verbs.
    /// </summary>
    /// <remarks>
    /// <b><c>--by</c> is required on both mutating verbs.</b> Minting a key is how access is granted
    /// and revoking one is how it is withdrawn, so neither is a thing the CLI may do anonymously: it
    /// has no identity to sign with, and inventing one would put a name in the audit record that
    /// nobody asserted. This is the same rule and the same reasoning as <c>quarantine release --by</c>.
    /// </remarks>
    private static bool TryParseKey(string[] rest, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (rest.Length == 0)
        {
            error = "Usage: key create|list|revoke";
            return false;
        }

        switch (rest[0].ToLowerInvariant())
        {
            case "create":
            {
                if (Flag(rest, "--principal") is not { Length: > 0 } principal)
                {
                    error = "key create requires --principal <id>: a key belongs to an identity.";
                    return false;
                }

                if (Flag(rest, "--tenant") is not { Length: > 0 } tenant)
                {
                    error = "key create requires --tenant <id>: a principal is always tenant-scoped.";
                    return false;
                }

                if (Flag(rest, "--privileges") is not { Length: > 0 } privileges)
                {
                    error = "key create requires --privileges <a,b[,c]>.";
                    return false;
                }

                if (Flag(rest, "--by") is not { Length: > 0 } createdBy)
                {
                    error = "key create requires --by <principal> so the mint is attributed.";
                    return false;
                }

                command = new KeyCreateCommand(
                    principal,
                    tenant,
                    [.. privileges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    Flags(rest, "--sender"),
                    createdBy);

                return true;
            }

            case "list":
                command = new KeyListCommand(rest.Contains("--json"));
                return true;

            case "revoke":
            {
                if (Flag(rest, "--principal") is not { Length: > 0 } principal)
                {
                    error = "key revoke requires --principal <id>.";
                    return false;
                }

                if (Flag(rest, "--by") is not { Length: > 0 } revokedBy)
                {
                    error = "key revoke requires --by <principal> so the revocation is attributed.";
                    return false;
                }

                command = new KeyRevokeCommand(principal, revokedBy);
                return true;
            }

            default:
                error = $"Unknown key subcommand '{rest[0]}'. Expected create, list or revoke.";
                return false;
        }
    }

    private static bool TryParseQuarantine(string[] rest, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (rest.Length == 0)
        {
            error = "Usage: quarantine list|release";
            return false;
        }

        var tenant = Flag(rest, "--tenant");
        if (tenant is not { Length: > 0 })
        {
            error = "quarantine commands require --tenant <id>: quarantined mail is never listed unscoped.";
            return false;
        }

        switch (rest[0].ToLowerInvariant())
        {
            case "list":
                command = new QuarantineListCommand(tenant, rest.Contains("--json"));
                return true;

            case "release":
                if (rest.Length < 2 || rest[1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "Usage: quarantine release <queue-id> --tenant <id> --by <principal>";
                    return false;
                }

                if (Flag(rest, "--by") is not { Length: > 0 } by)
                {
                    // A release is audited. With nobody to attribute it to, there is nothing to
                    // record, so the caller supplies an identity rather than the CLI inventing one.
                    error = "quarantine release requires --by <principal> so the release can be attributed.";
                    return false;
                }

                command = new QuarantineReleaseCommand(tenant, rest[1], by);
                return true;

            default:
                error = $"Unknown quarantine subcommand '{rest[0]}'.";
                return false;
        }
    }

    private static bool TryPositional(string[] args, out string value, out string? error)
    {
        error = null;
        value = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;

        if (value.Length > 0)
        {
            return true;
        }

        error = "A file or directory argument is required.";
        return false;
    }

    /// <summary>Every value given for a flag that may legitimately repeat, in order.</summary>
    private static List<string> Flags(string[] args, string name)
    {
        var values = new List<string>();

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                values.Add(args[i + 1]);
            }
        }

        return values;
    }

    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Runs a non-web command against a CLI-only service provider.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var command, out var error))
        {
            await output.WriteLineAsync(error);
            return 2;
        }

        if (command is ServeCommand)
        {
            // Handled by the entry point, which owns the web host.
            return 0;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddStyloMailHost(builder.Configuration);

        using var host = builder.Build();
        await HostServices.InitialiseStorageAsync(host.Services, cancellationToken);

        // Same reason as the web entry point: resolve the assessor now so a half-configured
        // deployment fails on the command rather than on the first message it tries to assess.
        _ = host.Services.GetRequiredService<StyloMail.Core.IMailAssessor>();

        return command switch
        {
            AssessCommand assess => await CliCommands.AssessAsync(host.Services, assess, output, cancellationToken),
            ReplayCommand replay => await CliCommands.ReplayAsync(host.Services, replay, output, cancellationToken),
            QuarantineListCommand list => await CliCommands.QuarantineListAsync(host.Services, list, output, cancellationToken),
            QuarantineReleaseCommand release => await CliCommands.QuarantineReleaseAsync(host.Services, release, output, cancellationToken),
            ProfilesInspectCommand profiles => await CliCommands.ProfilesInspectAsync(host.Services, profiles, output, cancellationToken),
            KeyCreateCommand create => await KeyCommands.CreateAsync(host.Services, create, output, cancellationToken),
            KeyListCommand list => await KeyCommands.ListAsync(host.Services, list, output, cancellationToken),
            KeyRevokeCommand revoke => await KeyCommands.RevokeAsync(host.Services, revoke, output, cancellationToken),
            _ => 2,
        };
    }
}
