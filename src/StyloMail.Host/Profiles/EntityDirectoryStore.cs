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

    private const string OutboundSuffix = "#outbound";
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
                DisplayLabelReason = label is null ? SummaryReasons.NoStoredLabel : null,
                CompanyId = companyId,
                CompanyName = companyName,
                Domain = null,
                DomainReason = SummaryReasons.DirectionAgnosticScope,
                LastContactAt = volume?.LastObservedAt,
                LastContactReason = volume is null ? VolumeReason() : null,
                Volume = SenderVolume(volume),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(
            tenantId, "sender", entities, hasMore, rows.Count, cursor is null);
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

    private static EntityDirectoryResponse ReadRecipients(
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

        var trimmed = TrimPage(rows, limit, out var hasMore);

        var entities = new List<EntitySummaryResponse>(trimmed.Count);
        foreach (var row in trimmed)
        {
            entities.Add(new EntitySummaryResponse
            {
                ScopeId = row.Key,
                Kind = "recipient",
                Direction = row.Direction,
                DisplayLabel = null,
                DisplayLabelAvailability = SummaryAvailability.NotProduced,
                DisplayLabelReason = SummaryReasons.NoStoredLabel,
                CompanyId = null,
                CompanyName = null,
                Domain = null,
                DomainReason = SummaryReasons.DirectionAgnosticScope,
                LastContactAt = row.LastObservedAt,
                LastContactReason = row.LastObservedAt is null
                    ? SummaryReasons.NoProfileRecorded
                    : null,
                Volume = RecipientVolume(row),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(
            tenantId, "recipient", entities, hasMore, rows.Count, cursor is null);
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

        return DirectoryResponse(tenantId, "domain", entities, hasMore, rows.Count, cursor is null);
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
                CompanyId = companyId,
                CompanyName = name,
                Domain = null,
                DomainReason = SummaryReasons.DirectionAgnosticScope,
                LastContactAt = null,
                LastContactReason = SummaryReasons.VolumeNotComputed,
                Volume = UnavailableVolume(SummaryReasons.VolumeNotComputed),
                Evidence = NotComputedEvidence(),
            });
        }

        return DirectoryResponse(tenantId, "company", entities, hasMore, rows.Count, cursor is null);
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
