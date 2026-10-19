using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using StyloMail.Adaptive.Profiles;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Profiles;

/// <summary>Which population <c>GET /v1/entities</c> is enumerating.</summary>
public enum EntityDirectoryKind
{
    Sender = 0,
    Recipient = 1,
    Domain = 2,
    Company = 3,
}

/// <summary>
/// A bounded, tenant-scoped directory over the profiles the Host already keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Populations, not queue rows.</b> A directory built by paging the attention queue would present
/// a snapshot of what needs a decision as though it were everyone this tenant deals with, and the
/// people who never triggered anything would be invisible. Each kind therefore reads the store that
/// already owns that population: <c>sender_profile</c> for sending accounts, the adaptive profile
/// table for correspondents and domains, and <c>company</c> for companies.
/// </para>
/// <para>
/// <b>No label is invented.</b> Correspondent keys are keyed hashes, so they have no human label in
/// the store; the row says <c>no_stored_label</c> and the console shortens the opaque id. A domain
/// key is the domain. A sender label is the operator's own. No address is read, served or derived.
/// </para>
/// <para>
/// <b>Search is structural rather than escaped.</b> The query is bound as a parameter and matched
/// with <c>instr</c>, so a value containing <c>%</c> or <c>_</c> is the literal text the operator
/// typed rather than a pattern. The tenant predicate is never optional, and the page is one indexed
/// range plus <c>LIMIT</c>.
/// </para>
/// </remarks>
public sealed class EntityDirectoryStore
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxQueryLength = 128;

    /// <summary>
    /// How many recent envelopes one directory page may scan to recover a masked label.
    /// </summary>
    /// <remarks>
    /// The queue's raw envelope columns are not indexed by address, so the only bounded reverse
    /// lookup is "the most recent N messages". Five thousand recent recipients is far more than one
    /// page of profiles ordered by last observation, and a profile outside it gets a named gap
    /// rather than a guessed label. Overridable in the test assembly to exercise that boundary.
    /// </remarks>
    internal int MaskedLabelScanCap { get; init; } = 5_000;

    internal const string SenderSql =
        """
        SELECT p.principal_id, p.label, p.company_id, c.name
          FROM sender_profile p
          LEFT JOIN company c
            ON c.tenant_id = p.tenant_id AND c.company_id = p.company_id
         WHERE p.tenant_id = $tenant
           AND ($after IS NULL OR p.principal_id > $after)
           AND ($q IS NULL
                OR instr(lower(COALESCE(p.label, '')), $q) > 0
                OR instr(lower(p.principal_id), $q) > 0)
         ORDER BY p.principal_id
         LIMIT $limit;
        """;

    internal const string CompanySql =
        """
        SELECT company_id, name
          FROM company
         WHERE tenant_id = $tenant
           AND ($after IS NULL OR company_id > $after)
           AND ($q IS NULL
                OR instr(lower(name), $q) > 0
                OR instr(lower(company_id), $q) > 0)
         ORDER BY company_id
         LIMIT $limit;
        """;

    internal const string RecipientSql =
        """
        SELECT profile_scope, profile_key, direction, observed_attempts, last_observed_at
          FROM (
            SELECT profile_scope, profile_key, direction, observed_attempts, last_observed_at
              FROM profiles
             WHERE tenant_id = $tenant
               AND profile_scope = $scope_a
               AND last_observed_at IS NOT NULL
               AND ($after_at IS NULL
                    OR last_observed_at < $after_at
                    OR (last_observed_at = $after_at AND profile_key > $after_key))
               AND ($q IS NULL OR instr(lower(profile_key), $q) > 0)
             ORDER BY last_observed_at DESC, profile_key
             LIMIT $limit)
        UNION ALL
        SELECT profile_scope, profile_key, direction, observed_attempts, last_observed_at
          FROM (
            SELECT profile_scope, profile_key, direction, observed_attempts, last_observed_at
              FROM profiles
             WHERE tenant_id = $tenant
               AND profile_scope = $scope_b
               AND direction = $outbound
               AND last_observed_at IS NOT NULL
               AND ($after_at IS NULL
                    OR last_observed_at < $after_at
                    OR (last_observed_at = $after_at AND profile_key > $after_key))
               AND ($q IS NULL OR instr(lower(profile_key), $q) > 0)
             ORDER BY last_observed_at DESC, profile_key
             LIMIT $limit)
        ORDER BY last_observed_at DESC, profile_key
        LIMIT $limit;
        """;

    internal const string DomainSql =
        """
        SELECT profile_key, observed_attempts, last_observed_at
          FROM profiles
         WHERE tenant_id = $tenant
           AND profile_scope = $scope
           AND last_observed_at IS NOT NULL
           AND ($after_at IS NULL
                OR last_observed_at < $after_at
                OR (last_observed_at = $after_at AND profile_key > $after_key))
           AND ($q IS NULL OR instr(lower(profile_key), $q) > 0)
         ORDER BY last_observed_at DESC, profile_key
         LIMIT $limit;
        """;

    /// <summary>Sender volume and last contact, one statement for the whole page.</summary>
    internal const string SenderVolumeSql =
        """
        SELECT profile_key, observed_attempts, last_observed_at
          FROM profiles
         WHERE tenant_id = $tenant
           AND profile_scope = 'OutboundSender'
           AND profile_key IN ($keys);
        """;

    /// <summary>
    /// Recent inbound envelope senders. Index-backed by (tenant_id, created_at) and capped, so the
    /// reverse lookup never walks the tenant's whole queue history.
    /// </summary>
    internal const string InboundSenderCandidatesSql =
        """
        SELECT mail_from
          FROM queue_item
         WHERE tenant_id = $tenant AND direction = $inbound
         ORDER BY created_at DESC
         LIMIT $cap;
        """;

    /// <summary>
    /// Recent outbound envelope recipients, the raw counterpart of the hashed Recipient profile
    /// key. The item index drives the order; the recipient lookup is the table's primary key.
    /// </summary>
    internal const string OutboundRecipientCandidatesSql =
        """
        SELECT r.recipient_key
          FROM queue_recipient r
          JOIN queue_item i ON i.queue_id = r.queue_id
         WHERE i.tenant_id = $tenant AND i.direction = $outbound
         ORDER BY i.created_at DESC
         LIMIT $cap;
        """;

    private const string OutboundSuffix = "#outbound";
    private const string InboundSuffix = "#inbound";
    private const string QueueMaskedSource = "queue_envelope_masked";
    private const string InboundScope = "InboundSenderIdentity";
    private const string RecipientScope = "Recipient";
    private const string DomainScope = "DomainContext";

    private readonly HostDatabase _database;
    private readonly IAddressPseudonymSource _pseudonyms;

    public EntityDirectoryStore(HostDatabase database, IAddressPseudonymSource pseudonyms)
    {
        _database = database;
        _pseudonyms = pseudonyms;
    }

    public static bool TryParseKind(string? value, out EntityDirectoryKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "sender":
                kind = EntityDirectoryKind.Sender;
                return true;
            case "recipient":
                kind = EntityDirectoryKind.Recipient;
                return true;
            case "domain":
                kind = EntityDirectoryKind.Domain;
                return true;
            case "company":
                kind = EntityDirectoryKind.Company;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public EntityDirectoryResponse Read(
        string tenantId,
        EntityDirectoryKind kind,
        string? query,
        int limit,
        string? after,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        var cappedLimit = Math.Clamp(limit, 1, MaxLimit);
        var term = NormaliseQuery(query);

        var cursor = after is null
            ? null
            : EntityCursor.Decode(after, kind);

        try
        {
            using var connection = _database.Open();

            return kind switch
            {
                EntityDirectoryKind.Sender => ReadSenders(
                    connection, tenantId, term, cappedLimit, cursor, cancellationToken),
                EntityDirectoryKind.Recipient => ReadRecipients(
                    connection, tenantId, term, cappedLimit, cursor, cancellationToken),
                EntityDirectoryKind.Domain => ReadDomains(
                    connection, tenantId, term, cappedLimit, cursor, cancellationToken),
                EntityDirectoryKind.Company => ReadCompanies(
                    connection, tenantId, term, cappedLimit, cursor, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown entity kind."),
            };
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity directory could not be read.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Senders
    // ---------------------------------------------------------------------------------------------

    private EntityDirectoryResponse ReadSenders(
        SqliteConnection connection,
        string tenantId,
        string? term,
        int limit,
        EntityCursor? cursor,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string PrincipalId, string? Label, string? CompanyId, string? CompanyName)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = SenderSql;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$after", (object?)cursor?.Primary ?? DBNull.Value);
            command.Parameters.AddWithValue("$q", (object?)term ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        // Captured before the page is trimmed: this is how many rows the query returned, including
        // the extra probe row that proves a further page exists. A client counting rows it still
        // has to fetch needs this, not the page length.
        var scanned = rows.Count;

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveRange(limit, rows.Count - limit);
        }

        var volumes = ReadSenderVolumes(connection, tenantId, rows.Select(row => row.PrincipalId));

        var entities = new List<EntitySummaryResponse>(rows.Count);
        foreach (var (principalId, label, companyId, companyName) in rows)
        {
            volumes.TryGetValue(principalId, out var volume);

            entities.Add(new EntitySummaryResponse
            {
                ScopeId = principalId,
                Kind = "sender",
                Direction = MailDirection.Outbound,
                DisplayLabel = label,
                DisplayLabelAvailability = label is null
                    ? SummaryAvailability.NotProduced
                    : SummaryAvailability.Available,
                DisplayLabelSource = label is null ? null : "sender_profile",
                DisplayLabelReason = label is null ? SummaryReasons.NoStoredLabel : null,
                CompanyId = companyId,
                CompanyName = companyName,
                Domain = null,
                DomainReason = SummaryReasons.DomainNotRecorded,
                LastContactAt = volume?.LastObservedAt,
                LastContactReason = volume is null ? VolumeReason() : null,
                Volume = SenderVolume(volume),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(
            tenantId, "sender", entities, hasMore, scanned, cursor is null);
    }

    private Dictionary<string, SenderProfileRow> ReadSenderVolumes(
        SqliteConnection connection,
        string tenantId,
        IEnumerable<string> principalIds)
    {
        var result = new Dictionary<string, SenderProfileRow>(StringComparer.Ordinal);
        var hasher = _pseudonyms.Current;
        if (hasher is null)
        {
            return result;
        }

        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = new List<string>();

        foreach (var principalId in principalIds)
        {
            var key = hasher.Hash(tenantId, principalId) + OutboundSuffix;
            byKey[key] = principalId;
            parameters.Add(key);
        }

        if (parameters.Count == 0)
        {
            return result;
        }

        var names = new List<string>(parameters.Count);
        using var command = connection.CreateCommand();
        for (var index = 0; index < parameters.Count; index++)
        {
            var name = $"$k{index}";
            names.Add(name);
            command.Parameters.AddWithValue(name, parameters[index]);
        }

        command.CommandText = SenderVolumeSql.Replace("$keys", string.Join(", ", names), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$tenant", tenantId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!byKey.TryGetValue(key, out var principalId))
            {
                continue;
            }

            result[principalId] = new SenderProfileRow(
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : ParseStamp(reader.GetString(2)));
        }

        return result;
    }

    private EntityVolumeResponse SenderVolume(SenderProfileRow? row)
    {
        if (_pseudonyms.Current is null)
        {
            return UnavailableVolume(SummaryReasons.NoProfileKey);
        }

        if (row is null)
        {
            return UnavailableVolume(SummaryReasons.NoProfileRecorded);
        }

        return new EntityVolumeResponse
        {
            Messages = row.Attempts,
            Inbound = 0,
            Outbound = row.Attempts,
            Availability = SummaryAvailability.Available,
            Basis = "lifetime_profile_observations",
        };
    }

    private string? VolumeReason() =>
        _pseudonyms.Current is null ? SummaryReasons.NoProfileKey : SummaryReasons.NoProfileRecorded;

    // ---------------------------------------------------------------------------------------------
    // Recipients
    // ---------------------------------------------------------------------------------------------

    private EntityDirectoryResponse ReadRecipients(
        SqliteConnection connection,
        string tenantId,
        string? term,
        int limit,
        EntityCursor? cursor,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProfileRow>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = RecipientSql;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$scope_a", InboundScope);
            command.Parameters.AddWithValue("$scope_b", RecipientScope);
            command.Parameters.AddWithValue("$outbound", (int)MailDirection.Outbound);
            command.Parameters.AddWithValue("$q", (object?)term ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            BindCursor(command, cursor);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new ProfileRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : (MailDirection)reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : ParseStamp(reader.GetString(4))));
            }
        }

        var scanned = rows.Count;
        var trimmed = TrimPage(rows, limit, out var hasMore);

        // Resolved for this page only: the candidate scans are bounded and their cost should not be
        // paid for the probe row or for a page the caller is not reading.
        var masked = _pseudonyms.Current is null
            ? null
            : ReadMaskedLabels(connection, tenantId, trimmed, cancellationToken);

        var entities = new List<EntitySummaryResponse>(trimmed.Count);
        foreach (var row in trimmed)
        {
            var recovered = masked is not null && masked.TryGetValue(row.Key, out var known)
                ? known
                : null;

            var label = recovered?.Label;
            var reason = masked is null
                ? SummaryReasons.NoProfileKey
                : recovered is null
                    ? SummaryReasons.AddressNotInBoundedScan
                    : recovered.Reason;

            entities.Add(new EntitySummaryResponse
            {
                ScopeId = row.Key,
                Kind = "recipient",
                Direction = row.Direction,
                DisplayLabel = label,
                DisplayLabelAvailability = label is null
                    ? SummaryAvailability.NotProduced
                    : SummaryAvailability.Available,
                DisplayLabelSource = label is null ? null : QueueMaskedSource,
                DisplayLabelReason = label is null ? reason : null,
                CompanyId = null,
                CompanyName = null,
                Domain = recovered?.Domain,
                DomainReason = recovered?.Domain is null ? reason : null,
                LastContactAt = row.LastObservedAt,
                LastContactReason = row.LastObservedAt is null
                    ? SummaryReasons.NoProfileRecorded
                    : null,
                Volume = RecipientVolume(row),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(
            tenantId, "recipient", entities, hasMore, scanned, cursor is null);
    }

    private static EntityVolumeResponse RecipientVolume(ProfileRow row)
    {
        var inbound = row.Direction == MailDirection.Inbound ? row.Attempts : 0;
        var outbound = row.Direction == MailDirection.Outbound ? row.Attempts : 0;

        return new EntityVolumeResponse
        {
            Messages = row.Attempts,
            Inbound = inbound,
            Outbound = outbound,
            Availability = SummaryAvailability.Available,
            Basis = "lifetime_profile_observations",
            Reason = row.Direction is null ? SummaryReasons.DirectionAgnosticScope : null,
        };
    }

    /// <summary>
    /// Recovers a display-safe masked address for the page's correspondent rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile key is a tenant-keyed hash and cannot be reversed. The raw address does still
    /// exist in the queue's envelope columns, so the lookup runs the other way: read a bounded set
    /// of recent envelope addresses, hash each with the same hasher, and match the page's keys.
    /// </para>
    /// <para>
    /// <b>No payload is parsed and no full address leaves this method.</b> The candidate string is
    /// reduced to the mask before it is attached to a row; the raw value is local to this call.
    /// </para>
    /// </remarks>
    private Dictionary<string, MaskedRecipient> ReadMaskedLabels(
        SqliteConnection connection,
        string tenantId,
        IReadOnlyList<ProfileRow> rows,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MaskedRecipient>(StringComparer.Ordinal);
        var hasher = _pseudonyms.Current;
        if (hasher is null)
        {
            return result;
        }

        var needInbound = rows.Any(row =>
            row.Scope == InboundScope && InboundHash(row.Key) is not null);
        var needOutbound = rows.Any(row =>
            row.Scope == RecipientScope && row.Direction == MailDirection.Outbound
            && OutboundHash(row.Key) is not null);

        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        if (needInbound)
        {
            CollectCandidates(
                connection, InboundSenderCandidatesSql, tenantId, hasher, byHash, cancellationToken);
        }

        if (needOutbound)
        {
            CollectCandidates(
                connection, OutboundRecipientCandidatesSql, tenantId, hasher, byHash, cancellationToken);
        }

        foreach (var row in rows)
        {
            var hash = row.Scope switch
            {
                InboundScope => InboundHash(row.Key),
                RecipientScope when row.Direction == MailDirection.Outbound => OutboundHash(row.Key),
                _ => null,
            };

            if (hash is null || !byHash.TryGetValue(hash, out var address))
            {
                continue;
            }

            result[row.Key] = MaskAddress(address);
        }

        return result;
    }

    private void CollectCandidates(
        SqliteConnection connection,
        string sql,
        string tenantId,
        ProfileKeyHasher hasher,
        Dictionary<string, string> byHash,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$inbound", (int)MailDirection.Inbound);
        command.Parameters.AddWithValue("$outbound", (int)MailDirection.Outbound);
        command.Parameters.AddWithValue("$cap", MaskedLabelScanCap);

        using var reader = command.ExecuteReader();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var address = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(address))
            {
                // The assessor substitutes "<>" for an absent sender so the profile key exists.
                // Keep that identity here so the row can say "not maskable" rather than "outside
                // the scan", which are different facts.
                address = "<>";
            }

            if (!seen.Add(address))
            {
                continue;
            }

            byHash[hasher.Hash(tenantId, address)] = address;
        }
    }

    /// <summary>
    /// The hash component of an inbound-sender profile key: <c>hash|auth=...#inbound</c>.
    /// </summary>
    private static string? InboundHash(string key)
    {
        var withoutSuffix = key.EndsWith(InboundSuffix, StringComparison.Ordinal)
            ? key[..^InboundSuffix.Length]
            : key;

        var bar = withoutSuffix.IndexOf('|', StringComparison.Ordinal);
        var hash = bar > 0 ? withoutSuffix[..bar] : withoutSuffix;
        return hash.Length == 0 ? null : hash;
    }

    /// <summary>The hash component of an outbound-recipient profile key: <c>hash#outbound</c>.</summary>
    private static string? OutboundHash(string key)
    {
        var hash = key.EndsWith(OutboundSuffix, StringComparison.Ordinal)
            ? key[..^OutboundSuffix.Length]
            : key;

        return hash.Length == 0 ? null : hash;
    }

    /// <summary>
    /// First character of the local part plus the domain. Never the local part itself.
    /// </summary>
    /// <remarks>
    /// The mask keeps one character so two correspondents at the same domain are distinguishable,
    /// and keeps the domain because that is the part an operator can act on. A one-character local
    /// part still renders as <c>a***@domain</c>, so length is not disclosed. An address with no
    /// usable local part and domain (the null sender, or a malformed value) is not maskable and
    /// says so rather than rendering something misleading.
    /// </remarks>
    private static MaskedRecipient MaskAddress(string address)
    {
        var at = address.LastIndexOf('@');

        if (at <= 0 || at == address.Length - 1)
        {
            return new MaskedRecipient(null, null, SummaryReasons.AddressNotMaskable);
        }

        var local = address[..at];
        var domain = address[(at + 1)..].ToLowerInvariant();
        var first = local.EnumerateRunes().First().ToString();

        return new MaskedRecipient($"{first}***@{domain}", domain, null);
    }

    private sealed record MaskedRecipient(string? Label, string? Domain, string? Reason);

    // ---------------------------------------------------------------------------------------------
    // Domains
    // ---------------------------------------------------------------------------------------------

    private static EntityDirectoryResponse ReadDomains(
        SqliteConnection connection,
        string tenantId,
        string? term,
        int limit,
        EntityCursor? cursor,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProfileRow>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = DomainSql;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$scope", DomainScope);
            command.Parameters.AddWithValue("$q", (object?)term ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            BindCursor(command, cursor);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new ProfileRow(
                    DomainScope,
                    reader.GetString(0),
                    null,
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : ParseStamp(reader.GetString(2))));
            }
        }

        var scanned = rows.Count;
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveRange(limit, rows.Count - limit);
        }

        var entities = new List<EntitySummaryResponse>(rows.Count);
        foreach (var row in rows)
        {
            entities.Add(new EntitySummaryResponse
            {
                ScopeId = row.Key,
                Kind = "domain",
                Direction = null,
                DisplayLabel = row.Key,
                DisplayLabelAvailability = SummaryAvailability.Available,
                DisplayLabelSource = "domain",
                CompanyId = null,
                CompanyName = null,
                Domain = row.Key,
                LastContactAt = row.LastObservedAt,
                LastContactReason = row.LastObservedAt is null
                    ? SummaryReasons.NoProfileRecorded
                    : null,
                Volume = new EntityVolumeResponse
                {
                    Messages = row.Attempts,
                    Inbound = null,
                    Outbound = null,
                    Availability = SummaryAvailability.Available,
                    Basis = "lifetime_profile_observations",
                    Reason = SummaryReasons.DirectionAgnosticScope,
                },
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(tenantId, "domain", entities, hasMore, scanned, cursor is null);
    }

    // ---------------------------------------------------------------------------------------------
    // Companies
    // ---------------------------------------------------------------------------------------------

    private static EntityDirectoryResponse ReadCompanies(
        SqliteConnection connection,
        string tenantId,
        string? term,
        int limit,
        EntityCursor? cursor,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string CompanyId, string Name)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = CompanySql;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$after", (object?)cursor?.Primary ?? DBNull.Value);
            command.Parameters.AddWithValue("$q", (object?)term ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var scanned = rows.Count;
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveRange(limit, rows.Count - limit);
        }

        var entities = new List<EntitySummaryResponse>(rows.Count);
        foreach (var (companyId, name) in rows)
        {
            entities.Add(new EntitySummaryResponse
            {
                ScopeId = companyId,
                Kind = "company",
                Direction = null,
                DisplayLabel = name,
                DisplayLabelAvailability = SummaryAvailability.Available,
                DisplayLabelSource = "company",
                CompanyId = companyId,
                CompanyName = name,
                Domain = null,
                DomainReason = SummaryReasons.DomainNotRecorded,
                LastContactAt = null,
                LastContactReason = SummaryReasons.VolumeNotComputed,
                Volume = UnavailableVolume(SummaryReasons.VolumeNotComputed),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(tenantId, "company", entities, hasMore, scanned, cursor is null);
    }

    // ---------------------------------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------------------------------

    private static EntityDirectoryResponse DirectoryResponse(
        string tenantId,
        string kind,
        IReadOnlyList<EntitySummaryResponse> entities,
        bool hasMore,
        int scanned,
        bool firstPage)
    {
        var last = entities.Count == 0 ? null : entities[^1];

        return new EntityDirectoryResponse
        {
            TenantId = tenantId,
            Kind = kind,
            Availability = SummaryAvailability.Available,
            Entities = entities,
            NextCursor = hasMore
                ? EntityCursor.Encode(kind, last!)
                : null,
            HasMore = hasMore,
            Coverage = new EntityDirectoryCoverageResponse
            {
                // "Complete" answers whether this page is the whole population, not whether the
                // page itself is well formed. A further page means the reader has not seen everyone.
                Complete = !hasMore,
                Scanned = scanned,
                Note = CoverageNote(kind, firstPage),
            },
        };
    }

    private static string? CoverageNote(string kind, bool firstPage)
    {
        string? note = kind is "recipient" or "domain"
            ? "A profile row with no recorded last observation is not listed, because it has no "
                + "position in a most-recently-seen order and its last contact cannot be stated."
            : null;

        if (!firstPage)
        {
            var continuation = "Continuing from the supplied cursor; labels and counts are as of "
                + "this read.";
            note = note is null ? continuation : note + " " + continuation;
        }

        return note;
    }

    private static EntityEvidenceResponse NotComputedEvidence()
    {
        var member = new EntityEvidenceMemberResponse
        {
            Availability = SummaryAvailability.NotProduced,
            Reason = SummaryReasons.DirectoryEvidenceNotComputed,
        };

        return new EntityEvidenceResponse
        {
            Reply = member,
            Pressure = member,
            Identity = member,
        };
    }

    private static EntityVolumeResponse UnavailableVolume(string reason) => new()
    {
        Messages = null,
        Inbound = null,
        Outbound = null,
        Availability = SummaryAvailability.NotProduced,
        Basis = "lifetime_profile_observations",
        Reason = reason,
    };

    private static string? NormaliseQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.Trim();

        // Not an escape: the value is a bound parameter matched with instr(). The bound is a bound
        // on how much work one page's filter may represent, and on how much of a value is echoed in
        // an error message if a caller sends something absurd.
        return trimmed.Length <= MaxQueryLength
            ? trimmed.ToLowerInvariant()
            : trimmed[..MaxQueryLength].ToLowerInvariant();
    }

    private static void BindCursor(SqliteCommand command, EntityCursor? cursor)
    {
        command.Parameters.AddWithValue("$after_at", (object?)cursor?.Secondary ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$after_key", (object?)cursor?.Primary ?? DBNull.Value);
    }

    private static List<T> TrimPage<T>(List<T> rows, int limit, out bool hasMore)
    {
        hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveRange(limit, rows.Count - limit);
        }

        return rows;
    }

    private static DateTimeOffset? ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private sealed record SenderProfileRow(int Attempts, DateTimeOffset? LastObservedAt);

    private sealed record ProfileRow(
        string Scope,
        string Key,
        MailDirection? Direction,
        int Attempts,
        DateTimeOffset? LastObservedAt);

    /// <summary>
    /// An opaque cursor over one directory order.
    /// </summary>
    /// <remarks>
    /// <b>Kind-tagged.</b> A cursor minted for senders and replayed against recipients would name a
    /// different sort key, so the kind is part of the payload and a mismatch is refused rather than
    /// silently starting from a position that means something else in another population.
    /// </remarks>
    private sealed record EntityCursor(string Primary, string? Secondary)
    {
        private const string Prefix = "v1";

        public static string Encode(string kind, EntitySummaryResponse last)
        {
            // Sender and company orders are single-key; recipient and domain orders are
            // (last observed, key). The second component is empty for the single-key orders.
            var secondary = last.Kind is "recipient" or "domain"
                ? last.LastContactAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;

            var payload = string.Join('\u001f', Prefix, kind, last.ScopeId, secondary);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        public static EntityCursor Decode(string value, EntityDirectoryKind kind)
        {
            try
            {
                var padded = value.Replace('-', '+').Replace('_', '/');
                padded += new string('=', (4 - padded.Length % 4) % 4);

                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded))
                    .Split('\u001f');

                if (parts.Length != 4
                    || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
                    || !string.Equals(parts[1], kind.ToString().ToLowerInvariant(), StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(parts[2]))
                {
                    throw new ArgumentException(
                        "The cursor is not one this directory issued for this kind. Echo NextCursor "
                        + "unchanged, or omit it to start from the first page.",
                        nameof(value));
                }

                return new EntityCursor(parts[2], parts[3].Length == 0 ? null : parts[3]);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(
                    "The cursor is not a cursor this directory issued. Echo NextCursor unchanged.",
                    nameof(value),
                    ex);
            }
        }
    }
}
