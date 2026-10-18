using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Adaptive.Profiles;
using StyloMail.Core;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Profiles;

/// <summary>
/// Builds an entity, domain, company or conversation profile from the decision ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>Population first, then measurement.</b> Every KPI is computed over the messages attributable
/// to the scope in the window, and every value carries the definition that produced it plus the
/// coverage it saw. Nothing is served as a bare number.
/// </para>
/// <para>
/// <b>The queue join is a deliberate, read-only boundary.</b> The decision ledger does not persist
/// the envelope, so entity and domain attribution needs <c>queue_item.trusted_principal_id</c> and
/// <c>queue_item.mail_from</c>. This query reads those two tables in the same SQLite file and never
/// writes them. Assessment-only rows have no queue row and are therefore unattributable; they are
/// reported through <see cref="ProfileCoverage.TenantMessages"/> rather than silently dropped.
/// </para>
/// <para>
/// <b>No score, no colour, no intent.</b> The profile reports counts, shares and instants, plus an
/// explicit list of what it does not measure. Tone, friendliness, manipulativeness, a composite
/// score and any intent label are absent by design.
/// </para>
/// </remarks>
public sealed class ProfileQueryStore
{
    public const int DefaultWindowDays = 90;
    public const int MinimumWindowDays = 7;
    public const int MaximumWindowDays = 365;

    /// <summary>Hard row cap. A larger window is reported as truncated rather than silently sampled.</summary>
    public const int MaxMessages = 2000;

    /// <summary>How far after an inbound message an outbound address match is searched.</summary>
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromDays(7);

    private const double ConfirmingThreshold = 0.5;

    private readonly HostDatabase _database;
    private readonly IAddressPseudonymSource _pseudonyms;

    public ProfileQueryStore(HostDatabase database, IAddressPseudonymSource pseudonyms)
    {
        _database = database;
        _pseudonyms = pseudonyms;
    }

    /// <summary>
    /// The per-query row cap. <see cref="MaxMessages"/> in every deployment; overridable inside the
    /// test assembly so a cap-hit path can be exercised without seeding 2,001 rows.
    /// </summary>
    internal int ReadCap { get; init; } = MaxMessages;

    public Task<ProfileSnapshot> ReadAsync(
        string tenantId,
        PropertyScope scope,
        int windowDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Id);
        cancellationToken.ThrowIfCancellationRequested();

        var days = Math.Clamp(windowDays, MinimumWindowDays, MaximumWindowDays);
        var windowEnd = now;
        var windowStart = windowEnd.AddDays(-days);

        try
        {
            using var connection = _database.Open();

            var current = ReadRange(connection, tenantId, scope, windowStart, windowEnd, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var prior = ReadRange(
                connection,
                tenantId,
                scope,
                windowStart.AddDays(-days),
                windowStart,
                cancellationToken);

            return Task.FromResult(AttachTrends(current.Snapshot, prior.Snapshot));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The profile could not be read.", ex);
        }
    }

    /// <summary>Reads one window. The trend is a second call over the equal-length window before it.</summary>
    private WindowRead ReadRange(
        SqliteConnection connection,
        string tenantId,
        PropertyScope scope,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scopeCount = CountScope(connection, tenantId, scope, windowStart, windowEnd);
            var rows = ReadMessages(connection, tenantId, scope, windowStart, windowEnd, out _);
            var truncated = scopeCount > ReadCap;

            var parsed = new List<ParsedMessage>(rows.Count);
            var unparsable = 0;

            foreach (var row in rows)
            {
                // The parse is the long synchronous stretch after the query; check between rows so
                // a cancelled request does not keep deserialising up to two thousand payloads.
                if ((parsed.Count & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var assessment = Parse(row.Payload);
                if (assessment is null)
                {
                    unparsable++;
                    continue;
                }

                parsed.Add(new ParsedMessage(row, assessment));
            }

            // Oldest first for every computation that walks time. The SQL reads newest first so the
            // cap keeps the most recent messages rather than an arbitrary early slice.
            parsed.Reverse();

            var tenantMessages = scope.Kind == PropertyScopeKind.Conversation
                ? scopeCount
                : CountTenantMessages(connection, tenantId, windowStart, windowEnd);

            var hasher = _pseudonyms.Current;

            cancellationToken.ThrowIfCancellationRequested();

            var replyCandidates = new List<MessageRow>();
            var replyTruncated = false;

            if (scope.Kind is PropertyScopeKind.Entity or PropertyScopeKind.Company)
            {
                replyCandidates = ReadMessages(
                    connection,
                    tenantId,
                    scope,
                    windowStart,
                    windowEnd,
                    out replyTruncated,
                    onlyOutbound: true);
            }

            return new WindowRead(
                Build(
                    tenantId,
                    scope,
                    windowStart,
                    windowEnd,
                    parsed,
                    unparsable,
                    truncated,
                    tenantMessages,
                    hasher,
                    replyCandidates,
                    replyTruncated,
                    ReadDisplayLabel(connection, tenantId, scope),
                    cancellationToken),
                parsed,
                replyCandidates,
                replyTruncated);
        }
    }

    /// <summary>One window's snapshot plus the parsed rows the evidence read needs.</summary>
    private sealed record WindowRead(
        ProfileSnapshot Snapshot,
        List<ParsedMessage> Parsed,
        List<MessageRow> ReplyCandidates,
        bool ReplyCandidatesTruncated);

    // ---------------------------------------------------------------------------------------------
    // Building the snapshot
    // ---------------------------------------------------------------------------------------------

    private static ProfileSnapshot Build(
        string tenantId,
        PropertyScope scope,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        List<ParsedMessage> parsed,
        int unparsable,
        bool truncated,
        int tenantMessages,
        ProfileKeyHasher? hasher,
        List<MessageRow> replyCandidates,
        bool replyCandidatesTruncated,
        (string? Label, string? Source, string? Reason) displayLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var total = parsed.Count;
        var coverage = new ProfileCoverage
        {
            Messages = total,
            TenantMessages = tenantMessages,
            Truncated = truncated,
            Unparsable = unparsable,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            FirstMessageAt = parsed.Count == 0 ? null : parsed[0].Row.RecordedAt,
            LastMessageAt = parsed.Count == 0 ? null : parsed[^1].Row.RecordedAt,
            Reason = total == 0 ? KpiReasons.NoAttributableMessages : null,
        };

        var counts = new KpiCounts(parsed, cancellationToken);
        var counterparties = CounterpartyKeys(tenantId, parsed, hasher);
        var conversationCount = parsed
            .Select(message => message.Row.ThreadId)
            .Where(thread => !string.IsNullOrWhiteSpace(thread))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var directionKnown = scope.Kind != PropertyScopeKind.Conversation && total > 0;

        var relationship = new ProfileRelationship
        {
            FirstSeenAt = coverage.FirstMessageAt,
            LastSeenAt = coverage.LastMessageAt,
            InboundMessages = directionKnown ? counts.InboundMessages : null,
            OutboundMessages = directionKnown ? counts.OutboundMessages : null,
            DirectionReason = directionKnown
                ? null
                : scope.Kind == PropertyScopeKind.Conversation
                    ? KpiReasons.NoIdentityCaptured
                    : KpiReasons.NoAttributableMessages,
            DistinctCounterparties = hasher is null ? null : counterparties.Count,
            DistinctCounterpartiesReason = hasher is null ? KpiReasons.NoProfileKey : null,
            Conversations = conversationCount > 0 ? conversationCount : null,
            ConversationsReason = conversationCount > 0 ? null : KpiReasons.NoThreadIdentity,
        };

        var kpis = new List<KpiValue>
        {
            BuildResponsiveness(
                tenantId, scope, parsed, replyCandidates, replyCandidatesTruncated, hasher,
                total, truncated, windowStart, windowEnd),
            BuildMomentum(parsed, total, truncated, windowStart, windowEnd),
            BuildSignalPrevalence(
                KpiDefinitions.Pressure,
                counts.PressureConfirming,
                counts.PressureAnswered,
                total, truncated, windowStart, windowEnd),
            BuildSignalPrevalence(
                KpiDefinitions.SensitiveRequests,
                counts.SensitiveConfirming,
                counts.SensitiveAnswered,
                total, truncated, windowStart, windowEnd),
            BuildSignalPrevalence(
                KpiDefinitions.Consistency,
                counts.IdentityAnomalous,
                counts.IdentityAnswered,
                total, truncated, windowStart, windowEnd),
            BuildHandling(counts.Intervened, total, truncated, windowStart, windowEnd),
        };

        return new ProfileSnapshot
        {
            Scope = scope,
            DisplayLabel = displayLabel.Label,
            DisplayLabelAvailable = displayLabel.Label is not null,
            DisplayLabelSource = displayLabel.Source,
            DisplayLabelReason = displayLabel.Reason,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Coverage = coverage,
            Identities = BuildIdentities(scope, parsed),
            Relationship = relationship,
            Kpis = kpis,
            NotMeasured = NotMeasured(scope),
        };
    }

    private static IReadOnlyList<ProfileIdentity> BuildIdentities(
        PropertyScope scope,
        List<ParsedMessage> parsed)
    {
        if (scope.Kind == PropertyScopeKind.Conversation)
        {
            return [];
        }

        if (scope.Kind == PropertyScopeKind.Domain)
        {
            return parsed.Count == 0
                ? []
                :
                [
                    new ProfileIdentity
                    {
                        Kind = "domain",
                        Value = scope.Id,
                        Messages = parsed.Count,
                        FirstSeenAt = parsed[0].Row.RecordedAt,
                        LastSeenAt = parsed[^1].Row.RecordedAt,
                    },
                ];
        }

        return
        [
            .. parsed
                .Where(message => message.Row.Direction == MailDirection.Inbound)
                .Select(message => (Domain: DomainOf(message.Row.MailFrom), message.Row.RecordedAt))
                .Where(entry => entry.Domain is not null)
                .GroupBy(entry => entry.Domain!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProfileIdentity
                {
                    Kind = "correspondent_domain",
                    Value = group.Key,
                    Messages = group.Count(),
                    FirstSeenAt = group.Min(entry => entry.RecordedAt),
                    LastSeenAt = group.Max(entry => entry.RecordedAt),
                })
                .OrderByDescending(identity => identity.Messages)
                .ThenBy(identity => identity.Value, StringComparer.Ordinal)
                .Take(10),
        ];
    }

    private static IReadOnlyList<ProfileNotMeasured> NotMeasured(PropertyScope scope)
    {
        var list = new List<ProfileNotMeasured>
        {
            new()
            {
                Id = "tone",
                Label = "Tone",
                Reason = "Not measured until calibrated with exemplars and a false-positive budget.",
            },
            new()
            {
                Id = "friendliness",
                Label = "Friendliness",
                Reason = "Not measured until calibrated with exemplars and a false-positive budget.",
            },
            new()
            {
                Id = "manipulativeness",
                Label = "Manipulativeness",
                Reason = "Not measured; a per-person judgement is the thing the spec forbids.",
            },
            new()
            {
                Id = "composite_score",
                Label = "Composite score",
                Reason = "Deliberately not produced. There is no trust, friendliness or risk score.",
            },
            new()
            {
                Id = "intent",
                Label = "Intent",
                Reason = "Deliberately not produced. Patterns are evidence; intent is never a verdict.",
            },
            new()
            {
                Id = "display_name_drift",
                Label = "Display-name drift",
                Reason = "Not captured in the decision ledger.",
            },
            new()
            {
                Id = "signature_drift",
                Label = "Signature drift",
                Reason = "Not captured in the decision ledger.",
            },
            new()
            {
                Id = "reply_threading",
                Label = "True In-Reply-To threading",
                Reason = "Not recorded. Responsiveness is an address-reciprocity proxy.",
            },
        };

        if (scope.Kind == PropertyScopeKind.Domain)
        {
            list.Add(new ProfileNotMeasured
            {
                Id = "outbound_to_domain",
                Label = "Outbound messages to this domain",
                Reason = "Recipient identity is not recoverable from the queue; domain scope is inbound-only.",
            });
        }

        if (scope.Kind == PropertyScopeKind.Conversation)
        {
            list.Add(new ProfileNotMeasured
            {
                Id = "envelope_direction",
                Label = "Inbound and outbound split",
                Reason = "Chat decisions carry no envelope direction.",
            });
        }

        return list;
    }

    /// <summary>
    /// A display-safe label that was already stored for this scope.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is derived here.</b> An entity label comes from the operator's sender profile, a
    /// company label from the company record, and a domain label is the domain the caller named.
    /// Message content and hashed identifiers are never read to invent a name. A scope with no
    /// stored label returns null plus <c>no_stored_label</c>, and the UI renders "Entity &lt;opaque&gt;".
    /// The lookup is tenant-scoped like every other read in this store.
    /// </remarks>
    private static (string? Label, string? Source, string? Reason) ReadDisplayLabel(
        SqliteConnection connection,
        string tenantId,
        PropertyScope scope)
    {
        if (scope.Kind == PropertyScopeKind.Domain)
        {
            return (scope.Id, "domain", null);
        }

        if (scope.Kind == PropertyScopeKind.Conversation)
        {
            return (null, null, KpiReasons.NoStoredLabel);
        }

        using var command = connection.CreateCommand();
        command.CommandText = scope.Kind == PropertyScopeKind.Entity
            ? "SELECT label FROM sender_profile WHERE tenant_id = $tenant AND principal_id = $id;"
            : "SELECT name FROM company WHERE tenant_id = $tenant AND company_id = $id;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", scope.Id);

        var stored = command.ExecuteScalar() as string;

        if (string.IsNullOrWhiteSpace(stored))
        {
            return (null, null, KpiReasons.NoStoredLabel);
        }

        return (
            stored.Trim(),
            scope.Kind == PropertyScopeKind.Entity ? "sender_profile" : "company",
            null);
    }

    private static ProfileSnapshot AttachTrends(ProfileSnapshot current, ProfileSnapshot prior)
    {
        var previous = prior.Kpis.ToDictionary(kpi => kpi.Definition.Id, StringComparer.Ordinal);

        return current with
        {
            Kpis =
            [
                .. current.Kpis.Select(kpi => kpi with
                {
                    Trend = BuildTrend(
                        kpi,
                        previous[kpi.Definition.Id],
                        prior.WindowStart,
                        prior.WindowEnd,
                        prior.Coverage.Truncated),
                }),
            ],
        };
    }

    /// <summary>
    /// Compares a value with the same definition over the equal-length prior window.
    /// </summary>
    /// <remarks>
    /// <b>A movement is never inferred from the current value alone.</b> Both windows must be
    /// Produced under the same definition version; an empty or thin prior window is
    /// NotEnoughHistory, and a prior value that was not produced is NotProduced. Flat means exactly
    /// equal because a tolerance band would be an invented threshold.
    /// </remarks>
    private static KpiTrend BuildTrend(
        KpiValue current,
        KpiValue prior,
        DateTimeOffset priorWindowStart,
        DateTimeOffset priorWindowEnd,
        bool priorTruncated)
    {
        KpiTrend Refused(KpiTrendState state, string reason, string detail) => new()
        {
            State = state,
            PriorValue = prior.Value,
            PriorWindowStart = priorWindowStart,
            PriorWindowEnd = priorWindowEnd,
            PriorTruncated = priorTruncated,
            Reason = reason,
            Detail = detail,
        };

        if (current.State == KpiState.NotEnoughHistory)
        {
            return Refused(
                KpiTrendState.NotEnoughHistory,
                KpiReasons.CurrentWindowNotEnoughHistory,
                $"The current window needs at least {current.Definition.MinimumSample} observations.");
        }

        if (current.State != KpiState.Produced || current.Value is null)
        {
            return Refused(
                KpiTrendState.NotProduced,
                KpiReasons.CurrentWindowNotProduced,
                current.Detail ?? "The current window did not produce a value.");
        }

        if (!string.Equals(current.Definition.Version, prior.Definition.Version, StringComparison.Ordinal))
        {
            return Refused(
                KpiTrendState.NotProduced,
                KpiReasons.DefinitionVersionChanged,
                "The definition version changed between the two windows, so the values are not "
                + "comparable.");
        }

        if (prior.Coverage.Total == 0)
        {
            return Refused(
                KpiTrendState.NotEnoughHistory,
                KpiReasons.PriorWindowEmpty,
                "The prior window had no attributable messages.");
        }

        if (prior.State == KpiState.NotEnoughHistory)
        {
            return Refused(
                KpiTrendState.NotEnoughHistory,
                KpiReasons.PriorWindowNotEnoughHistory,
                $"The prior window analysed {prior.Coverage.Analysed}; the definition needs at least "
                + $"{prior.Definition.MinimumSample}.");
        }

        if (prior.State == KpiState.ReducedCoverage)
        {
            return Refused(
                KpiTrendState.NotProduced,
                KpiReasons.PriorWindowReducedCoverage,
                "The prior window's own coverage was reduced, so it is not a comparison baseline.");
        }

        if (prior.State != KpiState.Produced || prior.Value is null)
        {
            return Refused(
                KpiTrendState.NotProduced,
                KpiReasons.PriorWindowNotProduced,
                prior.Detail ?? "The prior window did not produce a value.");
        }

        var priorValue = prior.Value.Value;
        var delta = current.Value.Value - priorValue;

        return new KpiTrend
        {
            State = KpiTrendState.Produced,
            PriorValue = priorValue,
            Delta = delta,
            RelativeDelta = priorValue == 0 ? null : delta / Math.Abs(priorValue),
            Movement = delta > 0
                ? KpiTrendMovement.Up
                : delta < 0
                    ? KpiTrendMovement.Down
                    : KpiTrendMovement.Flat,
            PriorWindowStart = priorWindowStart,
            PriorWindowEnd = priorWindowEnd,
            PriorTruncated = priorTruncated,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // KPI construction
    // ---------------------------------------------------------------------------------------------

    private static KpiValue BuildResponsiveness(
        string tenantId,
        PropertyScope scope,
        List<ParsedMessage> parsed,
        List<MessageRow> replyCandidates,
        bool replyCandidatesTruncated,
        ProfileKeyHasher? hasher,
        int total,
        bool truncated,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var definition = KpiDefinitions.Require(KpiDefinitions.Responsiveness);

        KpiValue Refuse(string reason, string detail) => Refused(
            definition, total, truncated, windowStart, windowEnd, reason, detail);

        if (total == 0)
        {
            return Refuse(KpiReasons.NoAttributableMessages, "No messages are attributable to this scope.");
        }

        if (scope.Kind == PropertyScopeKind.Domain)
        {
            // Inbound-only by ruling. The recipient side is not attributable without reconstructing
            // domains from hashed recipient keys, which this build does not do.
            return Refuse(
                KpiReasons.NoOutboundRecipientIdentity,
                "Domain profiles are inbound-only in v1, so no outbound address can be matched.");
        }

        if (scope.Kind == PropertyScopeKind.Conversation)
        {
            return Refuse(
                KpiReasons.NoAddressReciprocityInScope,
                "Chat conversations carry no envelope addresses to match.");
        }

        if (hasher is null)
        {
            return Refuse(
                KpiReasons.NoProfileKey,
                "No tenant profile key is configured, so addresses cannot be matched pseudonymously.");
        }

        var inbound = parsed
            .Where(message => message.Row.Direction == MailDirection.Inbound)
            .Where(message => !string.IsNullOrWhiteSpace(message.Row.MailFrom))
            .ToList();

        // A message younger than its full 7-day opportunity is "too early to know", not unanswered.
        // The strict bound also means every possible reply is before windowEnd, so the candidate
        // query never needs to look into the next window.
        var observableUntil = windowEnd.Subtract(ReplyWindow);
        var fullyObserved = inbound
            .Where(message => message.Row.RecordedAt < observableUntil)
            .ToList();
        var censored = inbound.Count - fullyObserved.Count;

        var outbound = new List<(DateTimeOffset At, string Counterparty)>();
        var unparsableTimes = new List<DateTimeOffset>();
        DateTimeOffset? oldestRetained = null;

        foreach (var candidate in replyCandidates)
        {
            oldestRetained = oldestRetained is null || candidate.RecordedAt < oldestRetained
                ? candidate.RecordedAt
                : oldestRetained;

            var assessment = Parse(candidate.Payload);
            if (assessment is null)
            {
                unparsableTimes.Add(candidate.RecordedAt);
                continue;
            }

            if (candidate.Direction != MailDirection.Outbound)
            {
                continue;
            }

            foreach (var disposition in assessment.RecipientDispositions)
            {
                if (!string.IsNullOrWhiteSpace(disposition.Recipient))
                {
                    outbound.Add((
                        candidate.RecordedAt,
                        hasher.Hash(tenantId, disposition.Recipient)));
                }
            }
        }

        outbound.Sort((left, right) => left.At.CompareTo(right.At));
        unparsableTimes.Sort();
        unparsableTimes.Reverse();

        var baseCoverage = new KpiCoverage
        {
            Analysed = 0,
            Total = total,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Truncated = truncated,
            Censored = censored,
            CandidateCount = replyCandidates.Count,
            CandidatesTruncated = replyCandidatesTruncated,
            CandidateUnparsable = unparsableTimes.Count,
        };

        if (inbound.Count == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = baseCoverage with { Reason = KpiReasons.SignalNotProduced },
                Reason = KpiReasons.SignalNotProduced,
                Detail = "No inbound message carried a counterparty address to match.",
            };
        }

        if (fullyObserved.Count == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = baseCoverage with { Reason = KpiReasons.NoFullyObservedInbound },
                Reason = KpiReasons.NoFullyObservedInbound,
                Detail = "Every inbound message is younger than its 7-day reply window; none is "
                    + "old enough to judge.",
            };
        }

        // A truncated candidate set keeps the newest rows, so candidates older than the oldest
        // retained row are missing. An inbound message whose window starts before that boundary
        // could have had its reply in the missing band, so it is excluded rather than counted as a
        // negative. An unreadable candidate excludes only messages whose window contains it.
        var judgeable = new List<ParsedMessage>();
        var excludedIncomplete = 0;

        foreach (var message in fullyObserved)
        {
            var limit = message.Row.RecordedAt.Add(ReplyWindow);

            var blockedByTruncation = replyCandidatesTruncated
                && oldestRetained is { } oldest
                && message.Row.RecordedAt < oldest;

            var blockedByUnparsable = unparsableTimes.Any(
                at => at > message.Row.RecordedAt && at <= limit);

            if (blockedByTruncation || blockedByUnparsable)
            {
                excludedIncomplete++;
                continue;
            }

            judgeable.Add(message);
        }

        var matched = 0;
        var gaps = new List<double>();

        foreach (var message in judgeable)
        {
            var counterparty = hasher.Hash(tenantId, message.Row.MailFrom!);
            var limit = message.Row.RecordedAt.Add(ReplyWindow);

            var match = outbound.FirstOrDefault(candidate =>
                candidate.At > message.Row.RecordedAt
                && candidate.At <= limit
                && string.Equals(candidate.Counterparty, counterparty, StringComparison.Ordinal));

            if (match.At != default)
            {
                matched++;
                gaps.Add((match.At - message.Row.RecordedAt).TotalHours);
            }
        }

        var denominator = judgeable.Count;
        var reduced = excludedIncomplete > 0 || replyCandidatesTruncated || unparsableTimes.Count > 0;

        var coverage = baseCoverage with
        {
            Analysed = denominator,
            Reason = denominator < definition.MinimumSample
                ? KpiReasons.NotEnoughHistory
                : reduced
                    ? KpiReasons.ReplyCandidatesIncomplete
                    : null,
        };

        if (denominator < definition.MinimumSample)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotEnoughHistory,
                Numerator = matched,
                Denominator = denominator,
                Coverage = coverage,
                Reason = KpiReasons.NotEnoughHistory,
                Detail = $"Needs at least {definition.MinimumSample} fully observed inbound messages "
                    + $"with an address; judged {denominator}"
                    + (excludedIncomplete > 0
                        ? $" and excluded {excludedIncomplete} because the candidate set was incomplete."
                        : "."),
            };
        }

        var measurements = new List<KpiMeasurement>();
        if (gaps.Count == 0)
        {
            measurements.Add(new KpiMeasurement
            {
                Id = "median_reply_gap",
                Label = "Median gap to the matched outbound message",
                Unit = KpiUnit.Hours,
                Reason = KpiReasons.NoMatchedPairs,
            });
        }
        else
        {
            gaps.Sort();
            var middle = gaps.Count / 2;
            var median = gaps.Count % 2 == 0
                ? (gaps[middle - 1] + gaps[middle]) / 2
                : gaps[middle];

            measurements.Add(new KpiMeasurement
            {
                Id = "median_reply_gap",
                Label = "Median gap to the matched outbound message",
                Unit = KpiUnit.Hours,
                Value = median,
            });
        }

        measurements.Add(new KpiMeasurement
        {
            Id = "recent_inbound_censored",
            Label = "Inbound messages too recent for their 7-day window",
            Unit = KpiUnit.Count,
            Value = censored,
        });

        if (excludedIncomplete > 0)
        {
            measurements.Add(new KpiMeasurement
            {
                Id = "inbound_excluded_incomplete_candidates",
                Label = "Inbound messages excluded because the candidate set was incomplete",
                Unit = KpiUnit.Count,
                Value = excludedIncomplete,
            });
        }

        return new KpiValue
        {
            Definition = definition,
            State = reduced ? KpiState.ReducedCoverage : KpiState.Produced,
            Value = (double)matched / denominator,
            Numerator = matched,
            Denominator = denominator,
            Coverage = coverage,
            Reason = reduced ? KpiReasons.ReplyCandidatesIncomplete : null,
            Detail = reduced
                ? "Part of the candidate set could not be judged, so only fully observed messages "
                    + "are in the denominator; unmatched messages were not counted as negatives."
                : null,
            Measurements = measurements,
        };
    }

    private static KpiValue BuildMomentum(
        List<ParsedMessage> parsed,
        int total,
        bool truncated,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var definition = KpiDefinitions.Require(KpiDefinitions.Momentum);

        var midpoint = windowStart.AddTicks((windowEnd - windowStart).Ticks / 2);
        var prior = parsed.Count(message => message.Row.RecordedAt < midpoint);
        var recent = parsed.Count(message => message.Row.RecordedAt >= midpoint);

        var coverage = new KpiCoverage
        {
            Analysed = total,
            Total = total,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Truncated = truncated,
            Reason = total == 0
                ? KpiReasons.NoAttributableMessages
                : prior < definition.MinimumSample
                    ? KpiReasons.NotEnoughHistory
                    : null,
        };

        if (total == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = coverage,
                Reason = KpiReasons.NoAttributableMessages,
                Detail = "No messages are attributable to this scope.",
            };
        }

        if (prior < definition.MinimumSample)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotEnoughHistory,
                Numerator = recent,
                Denominator = prior,
                Coverage = coverage,
                Reason = KpiReasons.NotEnoughHistory,
                Detail = $"Needs at least {definition.MinimumSample} messages in the prior half; "
                    + $"saw {prior}.",
            };
        }

        return new KpiValue
        {
            Definition = definition,
            State = KpiState.Produced,
            Value = (double)recent / prior,
            Numerator = recent,
            Denominator = prior,
            Coverage = coverage,
        };
    }

    private static KpiValue BuildSignalPrevalence(
        string id,
        int numerator,
        int denominator,
        int total,
        bool truncated,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var definition = KpiDefinitions.Require(id);

        var coverage = new KpiCoverage
        {
            Analysed = denominator,
            Total = total,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Truncated = truncated,
            Reason = total == 0
                ? KpiReasons.NoAttributableMessages
                : denominator == 0
                    ? KpiReasons.SignalNotProduced
                    : denominator < definition.MinimumSample
                        ? KpiReasons.NotEnoughHistory
                        : null,
        };

        if (total == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = coverage,
                Reason = KpiReasons.NoAttributableMessages,
                Detail = "No messages are attributable to this scope.",
            };
        }

        if (denominator == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = coverage,
                Reason = KpiReasons.SignalNotProduced,
                Detail = "The evidence this definition rests on was not produced for any message.",
            };
        }

        if (denominator < definition.MinimumSample)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotEnoughHistory,
                Numerator = numerator,
                Denominator = denominator,
                Coverage = coverage,
                Reason = KpiReasons.NotEnoughHistory,
                Detail = $"Needs at least {definition.MinimumSample} analysed messages; saw {denominator}.",
            };
        }

        return new KpiValue
        {
            Definition = definition,
            State = KpiState.Produced,
            Value = (double)numerator / denominator,
            Numerator = numerator,
            Denominator = denominator,
            Coverage = coverage,
        };
    }

    private static KpiValue BuildHandling(
        int intervened,
        int total,
        bool truncated,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var definition = KpiDefinitions.Require(KpiDefinitions.Handling);

        var coverage = new KpiCoverage
        {
            Analysed = total,
            Total = total,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Truncated = truncated,
            Reason = total == 0 ? KpiReasons.NoAttributableMessages : null,
        };

        if (total == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = coverage,
                Reason = KpiReasons.NoAttributableMessages,
                Detail = "No messages are attributable to this scope.",
            };
        }

        return new KpiValue
        {
            Definition = definition,
            State = KpiState.Produced,
            Value = (double)intervened / total,
            Numerator = intervened,
            Denominator = total,
            Coverage = coverage,
        };
    }

    private static KpiValue Refused(
        KpiDefinition definition,
        int total,
        bool truncated,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string reason,
        string detail)
    {
        return new KpiValue
        {
            Definition = definition,
            State = KpiState.NotProduced,
            Coverage = new KpiCoverage
            {
                Analysed = 0,
                Total = total,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                Truncated = truncated,
                Reason = reason,
            },
            Reason = reason,
            Detail = detail,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Counting evidence
    // ---------------------------------------------------------------------------------------------

    private sealed record ParsedMessage(MessageRow Row, MailAssessment Assessment);

    private sealed record MessageRow(
        DateTimeOffset RecordedAt,
        string? ThreadId,
        string? PrincipalId,
        string? MailFrom,
        MailDirection? Direction,
        string Payload);

    /// <summary>The counts every KPI shares, computed once per profile.</summary>
    private sealed class KpiCounts
    {
        public KpiCounts(List<ParsedMessage> messages, CancellationToken cancellationToken)
        {
            InboundMessages = messages.Count(message => message.Row.Direction == MailDirection.Inbound);
            OutboundMessages = messages.Count(message => message.Row.Direction == MailDirection.Outbound);

            var index = 0;

            foreach (var message in messages)
            {
                if ((index++ & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (Find(message.Assessment, "semantic.urgency_pressure") is { } pressure
                    && IsAnswered(pressure))
                {
                    PressureAnswered++;
                    if (pressure.Value >= ConfirmingThreshold)
                    {
                        PressureConfirming++;
                    }
                }

                var credential = Find(message.Assessment, "semantic.credential_request");
                var payment = Find(message.Assessment, "semantic.payment_redirection");
                var sensitiveAnswered = (credential is not null && IsAnswered(credential))
                    || (payment is not null && IsAnswered(payment));

                if (sensitiveAnswered)
                {
                    SensitiveAnswered++;
                    if ((credential is not null && IsAnswered(credential)
                            && credential.Value >= ConfirmingThreshold)
                        || (payment is not null && IsAnswered(payment)
                            && payment.Value >= ConfirmingThreshold))
                    {
                        SensitiveConfirming++;
                    }
                }

                var identitySignals = IdentitySignals
                    .Select(signal => (Signal: signal, Evidence: Find(message.Assessment, signal)))
                    .Where(entry => entry.Evidence is not null && IsAnswered(entry.Evidence))
                    .ToList();

                if (identitySignals.Count > 0)
                {
                    IdentityAnswered++;
                    if (identitySignals.Any(entry => Establishes(entry.Signal, entry.Evidence!)))
                    {
                        IdentityAnomalous++;
                    }
                }

                if (IsIntervention(message.Assessment))
                {
                    Intervened++;
                }
            }
        }

        public int InboundMessages { get; }

        public int OutboundMessages { get; }

        public int PressureAnswered { get; }

        public int PressureConfirming { get; }

        public int SensitiveAnswered { get; }

        public int SensitiveConfirming { get; }

        public int IdentityAnswered { get; }

        public int IdentityAnomalous { get; }

        public int Intervened { get; }
    }

    private static readonly string[] IdentitySignals =
    [
        "deterministic.display_name_address_mismatch",
        "deterministic.envelope_header_identity",
        "deterministic.reply_to_divergence",
        "deterministic.thread_header_consistency",
    ];

    private static readonly HashSet<string> BooleanIdentitySignals = new(StringComparer.Ordinal)
    {
        "deterministic.display_name_address_mismatch",
        "deterministic.reply_to_divergence",
    };

    private static Evidence? Find(MailAssessment assessment, string signalId)
        => assessment.Evidence.FirstOrDefault(
            evidence => string.Equals(evidence.SignalId, signalId, StringComparison.Ordinal));

    private static bool IsAnswered(Evidence evidence)
        => evidence.Value is not null
            && evidence.Availability is EvidenceAvailability.Available
                or EvidenceAvailability.ReducedCoverage;

    private static bool Establishes(string signalId, Evidence evidence)
        => evidence.Value is { } value
            && value > 0
            && (!BooleanIdentitySignals.Contains(signalId) || value >= ConfirmingThreshold);

    private static bool IsIntervention(MailAssessment assessment)
    {
        if (assessment.Action is MailAction.Hold or MailAction.Quarantine or MailAction.Review)
        {
            return true;
        }

        return assessment.RecipientDispositions.Any(disposition =>
            disposition.DeliveryState is DeliveryState.Held
                or DeliveryState.Quarantined
                or DeliveryState.Review);
    }

    private static HashSet<string> CounterpartyKeys(
        string tenantId,
        List<ParsedMessage> parsed,
        ProfileKeyHasher? hasher)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (hasher is null)
        {
            return keys;
        }

        foreach (var message in parsed)
        {
            if (!string.IsNullOrWhiteSpace(message.Row.MailFrom))
            {
                keys.Add(hasher.Hash(tenantId, message.Row.MailFrom));
            }

            foreach (var disposition in message.Assessment.RecipientDispositions)
            {
                if (!string.IsNullOrWhiteSpace(disposition.Recipient))
                {
                    keys.Add(hasher.Hash(tenantId, disposition.Recipient));
                }
            }
        }

        return keys;
    }

    private static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1
            ? address[(at + 1)..].Trim().ToLowerInvariant()
            : null;
    }

    // ---------------------------------------------------------------------------------------------
    // Evidence dereference
    // ---------------------------------------------------------------------------------------------

    private const int MaxEvidenceSources = 100;
    private const int MaxReferenceCitations = 50;

    /// <summary>
    /// The evidence behind one KPI at one scope, bounded and free of content.
    /// </summary>
    /// <remarks>
    /// This re-reads the current and prior windows and reports the same value the profile read
    /// serves, plus the per-signal counts and the opaque source assessment ids. It never returns a
    /// body, an address, a filename or a subject: the drawer explains the measurement, it does not
    /// open the message.
    /// </remarks>
    public Task<KpiEvidenceRead> ReadKpiEvidenceAsync(
        string tenantId,
        PropertyScope scope,
        string definitionId,
        int windowDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Id);
        cancellationToken.ThrowIfCancellationRequested();

        var definition = KpiDefinitions.Require(definitionId);
        var days = Math.Clamp(windowDays, MinimumWindowDays, MaximumWindowDays);

        try
        {
            using var connection = _database.Open();

            var current = ReadRange(
                connection, tenantId, scope, now.AddDays(-days), now, cancellationToken);
            var prior = ReadRange(
                connection,
                tenantId,
                scope,
                now.AddDays(-2 * days),
                now.AddDays(-days),
                cancellationToken);

            var currentSnapshot = AttachTrends(current.Snapshot, prior.Snapshot);

            var currentKpi = currentSnapshot.Kpis.Single(kpi => kpi.Definition.Id == definition.Id);
            var priorKpi = prior.Snapshot.Kpis.Single(kpi => kpi.Definition.Id == definition.Id);

            var sources = current.Parsed
                .Skip(Math.Max(0, current.Parsed.Count - MaxEvidenceSources))
                .Reverse()
                .Select(message => new EvidenceSourceRow(
                    message.Assessment.AssessmentId,
                    message.Row.RecordedAt,
                    message.Row.Direction,
                    message.Assessment.Action,
                    [.. message.Assessment.Evidence
                        .Where(IsAnswered)
                        .Select(evidence => evidence.SignalId)
                        .Distinct(StringComparer.Ordinal)
                        .Take(8)]))
                .ToList();

            return Task.FromResult(new KpiEvidenceRead(
                currentKpi,
                priorKpi,
                BuildSignals(definition.Id, currentKpi, current.Parsed),
                sources,
                current.Parsed.Count,
                current.Parsed.Count > MaxEvidenceSources));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The KPI evidence could not be read.", ex);
        }
    }

    private static IReadOnlyList<KpiSignalEvidence> BuildSignals(
        string definitionId,
        KpiValue value,
        List<ParsedMessage> parsed) => definitionId switch
        {
            KpiDefinitions.Responsiveness =>
            [
                new KpiSignalEvidence(
                    "address_reciprocity",
                    "Inbound messages with a fully observed 7-day window",
                    value.Coverage.Analysed,
                    value.Numerator ?? 0,
                    (int)(value.Measurements
                        .FirstOrDefault(measurement =>
                            measurement.Id == "inbound_excluded_incomplete_candidates")
                        ?.Value ?? 0),
                    value.Coverage.Censored ?? 0,
                    0,
                    value.Reason),
            ],
            KpiDefinitions.Momentum =>
            [
                new KpiSignalEvidence(
                    "message.volume",
                    "Attributable messages (numerator is the recent half)",
                    value.Coverage.Analysed,
                    value.Numerator ?? 0,
                    0,
                    0,
                    0,
                    value.Reason),
            ],
            KpiDefinitions.Pressure =>
            [
                CountSignal(parsed, "semantic.urgency_pressure", "Pressure language", deterministic: false),
            ],
            KpiDefinitions.SensitiveRequests =>
            [
                CountSignal(parsed, "semantic.credential_request", "Credential request", deterministic: false),
                CountSignal(parsed, "semantic.payment_redirection", "Payment redirection", deterministic: false),
            ],
            KpiDefinitions.Consistency =>
            [
                .. IdentitySignals.Select(signal =>
                    CountSignal(parsed, signal, signal, deterministic: true)),
            ],
            KpiDefinitions.Handling =>
            [
                new KpiSignalEvidence(
                    "decision.intervention",
                    "Messages policy intervened on",
                    value.Coverage.Analysed,
                    value.Numerator ?? 0,
                    0,
                    0,
                    0,
                    value.Reason),
            ],
            _ => [],
        };

    private static KpiSignalEvidence CountSignal(
        List<ParsedMessage> parsed,
        string signalId,
        string label,
        bool deterministic)
    {
        var answered = 0;
        var confirming = 0;
        var unavailable = 0;
        var notApplicable = 0;
        var missing = 0;

        foreach (var message in parsed)
        {
            var evidence = Find(message.Assessment, signalId);

            if (evidence is null)
            {
                missing++;
                continue;
            }

            if (IsAnswered(evidence))
            {
                answered++;
                if (deterministic
                    ? Establishes(signalId, evidence)
                    : evidence.Value >= ConfirmingThreshold)
                {
                    confirming++;
                }

                continue;
            }

            if (evidence.Availability == EvidenceAvailability.NotApplicable)
            {
                notApplicable++;
            }
            else
            {
                // Unavailable, or "answered" with no number: both are a failed answer, never a no.
                unavailable++;
            }
        }

        return new KpiSignalEvidence(
            signalId,
            label,
            answered,
            confirming,
            unavailable,
            notApplicable,
            missing,
            answered == 0 ? KpiReasons.SignalNotProduced : null);
    }

    /// <summary>
    /// Resolves a stored property evidence reference to its metadata, citing property rows and
    /// whether the source ledger row still exists. No content is returned.
    /// </summary>
    public Task<ReferenceEvidenceRead?> ReadReferenceAsync(
        string tenantId,
        EvidenceKind requestedKind,
        string referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();

            var citationCount = CountCitations(connection, tenantId, referenceId);
            var citations = ReadCitations(connection, tenantId, referenceId);
            var source = ReadReferenceSource(connection, tenantId, referenceId);

            if (citations.Count == 0 && source is null)
            {
                return Task.FromResult<ReferenceEvidenceRead?>(null);
            }

            var reference = citations
                .Select(citation => citation.Reference)
                .FirstOrDefault(candidate => candidate is not null)
                ?? new EvidenceReference { Id = referenceId, Kind = requestedKind };

            var outcome = reference.RetentionUntil is { } until && until < now
                ? ReferenceOutcome.Expired
                : reference.AccessOutcome switch
                {
                    EvidenceAccessOutcome.Withheld => ReferenceOutcome.Withheld,
                    EvidenceAccessOutcome.NotEvaluated => ReferenceOutcome.NotEvaluated,
                    _ => ReferenceOutcome.Resolved,
                };

            return Task.FromResult<ReferenceEvidenceRead?>(new ReferenceEvidenceRead(
                reference,
                outcome,
                [.. citations.Select(citation => citation.Citation)],
                citationCount,
                citationCount > MaxReferenceCitations,
                source));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The evidence reference could not be read.", ex);
        }
    }

    private static int CountCitations(
        SqliteConnection connection,
        string tenantId,
        string referenceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM host_entity_property p, json_each(p.evidence_refs) AS ref
            WHERE p.tenant_id = $tenant
              AND COALESCE(json_extract(ref.value, '$.id'),
                           CASE WHEN json_type(ref.value) = 'text' THEN ref.value END) = $id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", referenceId);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<(ReferenceCitation Citation, EvidenceReference? Reference)> ReadCitations(
        SqliteConnection connection,
        string tenantId,
        string referenceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.scope_kind, p.scope_id, p.property_key, p.value, p.source, p.set_by, p.set_at,
                   p.confirmed_by, p.confirmed_at, p.evidence_refs
            FROM host_entity_property p, json_each(p.evidence_refs) AS ref
            WHERE p.tenant_id = $tenant
              AND COALESCE(json_extract(ref.value, '$.id'),
                           CASE WHEN json_type(ref.value) = 'text' THEN ref.value END) = $id
            ORDER BY p.set_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", referenceId);
        command.Parameters.AddWithValue("$limit", MaxReferenceCitations);

        var citations = new List<(ReferenceCitation, EvidenceReference?)>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse<PropertyScopeKind>(reader.GetString(0), out var kind)
                || !Enum.TryParse<PropertySource>(reader.GetString(4), out var source))
            {
                continue;
            }

            var confirmedBy = reader.IsDBNull(7) ? null : reader.GetString(7);

            citations.Add((
                new ReferenceCitation(
                    new PropertyScope(kind, reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    source,
                    PropertySurfaceStates.For(source, confirmedBy),
                    reader.GetString(5),
                    ParseStamp(reader.GetString(6)),
                    confirmedBy,
                    reader.IsDBNull(8) ? null : ParseStamp(reader.GetString(8))),
                FindReference(reader.GetString(9), referenceId)));
        }

        return citations;
    }

    private static EvidenceReference? FindReference(string json, string referenceId)
    {
        try
        {
            var references = JsonSerializer.Deserialize<List<EvidenceReference>>(json, HostJson.Options);
            if (references is not null)
            {
                return references.FirstOrDefault(reference =>
                    string.Equals(reference.Id, referenceId, StringComparison.Ordinal));
            }
        }
        catch (JsonException)
        {
            // Legacy rows hold bare strings; fall through.
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<string>>(json);
            return legacy is not null
                && legacy.Contains(referenceId, StringComparer.Ordinal)
                    ? new EvidenceReference { Id = referenceId }
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReferenceSource? ReadReferenceSource(
        SqliteConnection connection,
        string tenantId,
        string referenceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT assessment_id, recorded_at, action
            FROM host_decision_ledger
            WHERE tenant_id = $tenant
              AND (assessment_id = $id OR internal_message_id = $id)
            ORDER BY recorded_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", referenceId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        MailAction? action = Enum.TryParse<MailAction>(reader.GetString(2), out var parsed)
            ? parsed
            : null;

        return new ReferenceSource(
            reader.GetString(0),
            ParseStamp(reader.GetString(1)),
            action);
    }

    // ---------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------

    private static MailAssessment? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<MailAssessment>(payload, HostJson.PersistedRead);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static (string Predicate, bool JoinsQueue) Filter(PropertyScope scope) => scope.Kind switch
    {
        PropertyScopeKind.Company => (
            "q.trusted_principal_id IN (SELECT principal_id FROM sender_profile "
            + "WHERE tenant_id = $tenant AND company_id = $id)",
            true),
        PropertyScopeKind.Domain => (
            "q.direction = $inbound AND lower(substr(q.mail_from, instr(q.mail_from, '@') + 1)) = $id",
            true),
        PropertyScopeKind.Entity => ("q.trusted_principal_id = $id", true),
        PropertyScopeKind.Conversation => ("l.thread_id = $id", false),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope.Kind, "Unknown scope kind."),
    };

    private List<MessageRow> ReadMessages(
        SqliteConnection connection,
        string tenantId,
        PropertyScope scope,
        DateTimeOffset from,
        DateTimeOffset to,
        out bool truncated,
        bool onlyOutbound = false)
    {
        var (predicate, joinsQueue) = Filter(scope);

        using var command = connection.CreateCommand();
        command.CommandText = joinsQueue
            ? """
              SELECT l.recorded_at, l.thread_id, q.trusted_principal_id, q.mail_from, q.direction,
                     l.payload
              FROM host_decision_ledger l
              JOIN queue_item q
                ON q.tenant_id = l.tenant_id AND q.internal_message_id = l.internal_message_id
              WHERE l.tenant_id = $tenant
                AND l.recorded_at >= $from AND l.recorded_at < $to
                AND
              """ + " " + predicate
              + (onlyOutbound ? " AND q.direction = $outbound" : string.Empty)
              + " ORDER BY l.recorded_at DESC LIMIT $limit;"
            : """
              SELECT l.recorded_at, l.thread_id, NULL, NULL, NULL, l.payload
              FROM host_decision_ledger l
              WHERE l.tenant_id = $tenant
                AND l.recorded_at >= $from AND l.recorded_at < $to
                AND
              """ + " " + predicate
              + " ORDER BY l.recorded_at DESC LIMIT $limit;";

        BindScope(command, tenantId, scope, joinsQueue);
        command.Parameters.AddWithValue("$from", Stamp(from));
        command.Parameters.AddWithValue("$to", Stamp(to));
        command.Parameters.AddWithValue("$limit", ReadCap + 1);

        if (onlyOutbound)
        {
            command.Parameters.AddWithValue("$outbound", (int)MailDirection.Outbound);
        }

        var rows = new List<MessageRow>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var direction = joinsQueue && !reader.IsDBNull(4)
                ? (MailDirection?)reader.GetInt32(4)
                : null;

            rows.Add(new MessageRow(
                ParseStamp(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                direction,
                reader.GetString(5)));
        }

        truncated = rows.Count > ReadCap;
        if (truncated)
        {
            // The cap keeps the newest rows; the extra row only proves the cap was hit.
            rows.RemoveRange(ReadCap, rows.Count - ReadCap);
        }

        return rows;
    }

    private static int CountScope(
        SqliteConnection connection,
        string tenantId,
        PropertyScope scope,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var (predicate, joinsQueue) = Filter(scope);

        using var command = connection.CreateCommand();
        command.CommandText = joinsQueue
            ? """
              SELECT COUNT(*)
              FROM host_decision_ledger l
              JOIN queue_item q
                ON q.tenant_id = l.tenant_id AND q.internal_message_id = l.internal_message_id
              WHERE l.tenant_id = $tenant
                AND l.recorded_at >= $from AND l.recorded_at < $to
                AND
              """ + " " + predicate + ";"
            : """
              SELECT COUNT(*)
              FROM host_decision_ledger l
              WHERE l.tenant_id = $tenant
                AND l.recorded_at >= $from AND l.recorded_at < $to
                AND
              """ + " " + predicate + ";";

        BindScope(command, tenantId, scope, joinsQueue);
        command.Parameters.AddWithValue("$from", Stamp(from));
        command.Parameters.AddWithValue("$to", Stamp(to));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountTenantMessages(
        SqliteConnection connection,
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM host_decision_ledger "
            + "WHERE tenant_id = $tenant AND recorded_at >= $from AND recorded_at < $to;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$from", Stamp(from));
        command.Parameters.AddWithValue("$to", Stamp(to));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void BindScope(
        SqliteCommand command,
        string tenantId,
        PropertyScope scope,
        bool joinsQueue)
    {
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", scope.Id);

        if (joinsQueue)
        {
            command.Parameters.AddWithValue("$inbound", (int)MailDirection.Inbound);
        }
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
