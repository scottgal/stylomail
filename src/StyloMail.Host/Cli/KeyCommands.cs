using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Auth;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Cli;

/// <summary>
/// The <c>key</c> verbs: minting, listing and revoking this host's own API keys.
/// </summary>
/// <remarks>
/// <para>
/// Minting is a bootstrap action rather than a screen, which is why it lives on the executable and
/// not in the operator console. It is also the only way to create a credential, which is why the
/// rules here are tighter than the rest of the CLI's: a key that leaks out of this command is a key
/// that leaked, and a revocation that does not take effect is not a revocation.
/// </para>
/// <para>
/// <b>The value is printed to the caller's stdout and to nothing else.</b> There is deliberately no
/// <c>--output</c>, no file flag, no JSON envelope carrying it and no log line mentioning it. Only a
/// digest is stored, so a key that is not captured from this one line is gone and has to be minted
/// again, which is the property that makes the store safe to copy.
/// </para>
/// </remarks>
public static class KeyCommands
{
    private const int Ok = 0;
    private const int BadInput = 2;

    /// <summary>
    /// The requested capability does not exist: this host cannot revoke a principal it does not own.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BadInput"/> because the caller did nothing wrong. Retrying, fixing
    /// the arguments or minting the key again will not change the answer; removing a configuration
    /// entry and restarting the host will, and the refusal says so.
    /// </remarks>
    private const int CapabilityUnavailable = 3;

    public static async Task<int> CreateAsync(
        IServiceProvider services,
        KeyCreateCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var privileges = ParsePrivileges(command.Privileges, out var badPrivilege);

        if (badPrivilege is not null)
        {
            await output.WriteLineAsync(
                $"error: '{badPrivilege}' is not a privilege this host recognises, so a key carrying "
                + $"it would grant nothing by that name. Valid privileges: "
                + $"{string.Join(", ", HostPrivileges.Names)}.");

            return BadInput;
        }

        if (privileges == HostPrivilege.None)
        {
            // An unprivileged key cannot reach a single route, so minting one creates a credential
            // that authenticates and can do nothing, which reads as a broken key rather than as the
            // empty grant it is.
            await output.WriteLineAsync(
                "error: --privileges is required and must name at least one privilege: "
                + $"{string.Join(", ", HostPrivileges.Names)}. A key with none of them authenticates "
                + "and can do nothing.");

            return BadInput;
        }

        var store = services.GetRequiredService<MintedPrincipalStore>();
        var clock = services.GetRequiredService<TimeProvider>();
        var material = MintedApiKey.Mint();

        var principal = new MintedPrincipal
        {
            KeyId = material.KeyId,
            PrincipalId = command.PrincipalId,
            TenantId = command.TenantId,
            Privileges = [.. command.Privileges.Select(name => name.Trim())],
            ApprovedSenderIdentities = [.. command.ApprovedSenderIdentities],
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = command.CreatedBy,
        };

        try
        {
            if (!store.Create(principal, material))
            {
                await output.WriteLineAsync(
                    $"error: '{command.PrincipalId}' already has a live minted key. Revoke it first, "
                    + "so the host never holds two valid keys for one identity with nothing saying "
                    + "which is current.");

                return BadInput;
            }
        }
        catch (StorageUnavailableException)
        {
            // Nothing was written and nothing is printed. Reporting a mint that did not land would
            // leave an operator holding a key that does not work, which is worse than an error.
            await output.WriteLineAsync(
                "error: the key could not be durably stored, so it has not been issued. Nothing was "
                + "written and no key was created.");

            return BadInput;
        }

        // The one line that matters, and the only place this value exists. Everything the caller
        // needs to act on is here; nothing below it repeats or narrows it.
        await output.WriteLineAsync(material.Value);
        await output.WriteLineAsync();
        await output.WriteLineAsync(
            "This key is shown once and will not be shown again. Only a digest of it is stored, so "
            + "it cannot be recovered or re-displayed: mint a new key if this one is lost.");

        await WriteShadowNoteAsync(services, command.PrincipalId, output);

        return Ok;
    }

    public static async Task<int> ListAsync(
        IServiceProvider services,
        KeyListCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<PrincipalInventoryEntry> inventory;

        try
        {
            inventory = services.GetRequiredService<PrincipalDirectory>().Inventory();
        }
        catch (StorageUnavailableException)
        {
            await output.WriteLineAsync("error: the principal store could not be read.");
            return BadInput;
        }

        if (command.AsJson)
        {
            // The projection names its fields, and PrincipalInventoryEntry has no key and no digest
            // to name. A store that grew one could not reach this output without someone adding it
            // here deliberately.
            await output.WriteLineAsync(JsonSerializer.Serialize(
                inventory.Select(entry => new
                {
                    principal = entry.PrincipalId,
                    tenant = entry.TenantId,
                    privileges = entry.Privileges,
                    source = entry.Source.ToString().ToLowerInvariant(),
                    // The same words the table uses, deliberately. Two vocabularies for one status
                    // is how a console learns a spelling the CLI never says.
                    status = Describe(entry.Status),
                    configuredAt = entry.ConfiguredAt,
                }),
                HostJson.Options));

            return Ok;
        }

        var rows = inventory
            .Select(entry => new Row(
                entry.PrincipalId,
                entry.TenantId,
                entry.Privileges.Count == 0 ? "-" : string.Join(", ", entry.Privileges),
                entry.Source == PrincipalSource.Store ? "store" : "environment",
                Describe(entry.Status)))
            .ToList();

        await Row.WriteTableAsync(output, rows);

        // Printed only when the table contains a row that needs it. A legend under every listing
        // would be read once and skimmed thereafter, which is how a row that means "this credential
        // is inert" stops being noticed.
        if (inventory.Any(entry => entry.Status == PrincipalStatus.ShadowedByStore))
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync(
                "'shadowed by store': the store holds a minted principal of this name, and the store "
                + "wins wholesale. This configuration entry authenticates nothing, with any key.");
        }

        if (inventory.Any(entry => entry.Status == PrincipalStatus.Revoked))
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync(
                "'revoked': the key no longer authenticates. The store keeps claiming the name, so a "
                + "configuration entry of the same name does not take over.");
        }

        return Ok;
    }

    public static async Task<int> RevokeAsync(
        IServiceProvider services,
        KeyRevokeCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var store = services.GetRequiredService<MintedPrincipalStore>();

        MintedPrincipal? stored;
        IReadOnlyList<PrincipalInventoryEntry> inventory;

        try
        {
            // The *live* row for this principal, not merely the first one named it. A principal
            // accumulates a row per mint, so searching by name alone finds the oldest, which after a
            // re-mint is the revoked one: every replacement key would report "already revoked" and
            // be permanently un-revocable.
            var rows = store.List();

            stored = rows.FirstOrDefault(p =>
                    !p.IsRevoked && string.Equals(p.PrincipalId, command.PrincipalId, StringComparison.Ordinal))
                ?? rows.FirstOrDefault(p =>
                    string.Equals(p.PrincipalId, command.PrincipalId, StringComparison.Ordinal));

            inventory = services.GetRequiredService<PrincipalDirectory>().Inventory();
        }
        catch (StorageUnavailableException)
        {
            await output.WriteLineAsync("error: the principal store could not be read.");
            return BadInput;
        }

        if (stored is null)
        {
            return await RefuseAsync(command.PrincipalId, inventory, output);
        }

        if (stored.IsRevoked)
        {
            // Not an error. The end state the caller asked for already holds, and a script that
            // retries a revocation should not be told it failed. Contrast `quarantine release`,
            // where finding nothing to release means the message was not where the caller thought
            // it was, which is a state mismatch worth a non-zero exit.
            await output.WriteLineAsync(
                $"'{command.PrincipalId}' was already revoked by {stored.RevokedBy} at "
                + $"{stored.RevokedAt:u}; its key has not authenticated since. Nothing to do.");

            await WriteShadowNoteAsync(services, command.PrincipalId, output);
            return Ok;
        }

        bool revoked;

        try
        {
            revoked = store.Revoke(command.PrincipalId, command.RevokedBy, services.GetRequiredService<TimeProvider>().GetUtcNow());
        }
        catch (StorageUnavailableException)
        {
            await output.WriteLineAsync("error: the revocation could not be durably recorded.");
            return BadInput;
        }

        if (!revoked)
        {
            // Something else revoked it between the read and the write. The desired end state holds.
            await output.WriteLineAsync($"'{command.PrincipalId}' is no longer live; nothing to do.");
            return Ok;
        }

        await output.WriteLineAsync(
            $"revoked '{command.PrincipalId}' as {command.RevokedBy}. The key stops authenticating on "
            + "the next request on every channel and in every process; it cannot be un-revoked.");

        await WriteShadowNoteAsync(services, command.PrincipalId, output);

        return Ok;
    }

    /// <summary>
    /// Explains a revocation this host is not entitled to make.
    /// </summary>
    /// <remarks>
    /// <b>The refusal names the configuration that owns the principal.</b> A refusal that does not
    /// say where to go is a no-op with better manners: the operator is left holding a credential they
    /// have decided to kill and a command that will not kill it, with nothing telling them what
    /// would.
    /// </remarks>
    private static async Task<int> RefuseAsync(
        string principalId,
        IReadOnlyList<PrincipalInventoryEntry> inventory,
        TextWriter output)
    {
        var configured = inventory.FirstOrDefault(entry =>
            entry.Source == PrincipalSource.Environment
            && string.Equals(entry.PrincipalId, principalId, StringComparison.Ordinal));

        if (configured is null)
        {
            await output.WriteLineAsync(
                $"error: no principal named '{principalId}' is minted on this host or configured in it.");

            return BadInput;
        }

        await output.WriteLineAsync(
            $"refused: '{principalId}' is configured by the host's environment at "
            + $"{configured.ConfiguredAt ?? "StyloMail:Auth:Principals"}, not minted on it. This "
            + "command only revokes keys the host issued; it will not report a revocation that did "
            + "not happen. To stop this principal authenticating, remove that configuration entry "
            + "from the host's environment and restart it: the principals section is read once at "
            + "startup, so removing the entry alone changes nothing until then. Minting a key under "
            + "the same name is the alternative, and it takes the name over wholesale: the "
            + "configuration entry stops authenticating immediately, without a restart.");

        return CapabilityUnavailable;
    }

    /// <summary>
    /// Says so when a name is held by both sources.
    /// </summary>
    /// <remarks>
    /// Both a mint and a revocation change what a configuration entry of the same name does, and
    /// neither would say so otherwise. This is guard (b) applied where it bites: two sources is
    /// acceptable only because the operator can see which one is answering.
    /// </remarks>
    private static async Task WriteShadowNoteAsync(
        IServiceProvider services,
        string principalId,
        TextWriter output)
    {
        IReadOnlyList<PrincipalInventoryEntry> inventory;

        try
        {
            inventory = services.GetRequiredService<PrincipalDirectory>().Inventory();
        }
        catch (StorageUnavailableException)
        {
            return;
        }

        var configured = inventory.FirstOrDefault(entry =>
            entry.Source == PrincipalSource.Environment
            && string.Equals(entry.PrincipalId, principalId, StringComparison.Ordinal));

        if (configured is not null)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync(
                $"note: '{principalId}' is also configured at "
                + $"{configured.ConfiguredAt ?? "StyloMail:Auth:Principals"}. The store wins wholesale, "
                + "so that configuration entry authenticates nothing while this name is claimed.");
        }
    }

    private static HostPrivilege ParsePrivileges(IReadOnlyList<string> names, out string? badPrivilege)
    {
        badPrivilege = null;

        foreach (var name in names)
        {
            var trimmed = name.Trim();

            if (!HostPrivileges.Names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                badPrivilege = trimmed;
                return HostPrivilege.None;
            }
        }

        return HostPrivileges.Parse(names);
    }

    private static string Describe(PrincipalStatus status) => status switch
    {
        PrincipalStatus.Active => "active",
        PrincipalStatus.Revoked => "revoked",
        PrincipalStatus.ReadOnly => "read-only",
        PrincipalStatus.ShadowedByStore => "shadowed by store",
        _ => status.ToString().ToLowerInvariant(),
    };

    /// <summary>One rendered table row, padded into columns wide enough for their contents.</summary>
    private sealed record Row(string Principal, string Tenant, string Privileges, string Source, string Status)
    {
        public static async Task WriteTableAsync(TextWriter output, IReadOnlyList<Row> rows)
        {
            if (rows.Count == 0)
            {
                await output.WriteLineAsync(
                    "No principals are minted on this host or configured in it, so nothing can "
                    + "authenticate. Mint one with `stylomail key create`.");

                return;
            }

            var headers = new Row("principal", "tenant", "privileges", "source", "status");
            var all = new List<Row> { headers };
            all.AddRange(rows);

            var widths = new int[5];

            foreach (var row in all)
            {
                var cells = row.Cells();

                for (var i = 0; i < cells.Length; i++)
                {
                    widths[i] = Math.Max(widths[i], cells[i].Length);
                }
            }

            foreach (var row in all)
            {
                var cells = row.Cells();

                await output.WriteLineAsync(string.Join(
                    "  ",
                    cells.Select((cell, i) => i == cells.Length - 1 ? cell : cell.PadRight(widths[i]))));
            }
        }

        private string[] Cells() => [Principal, Tenant, Privileges, Source, Status];
    }
}
