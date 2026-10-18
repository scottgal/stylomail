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

            var scopeCount = CountScope(connection, tenantId, scope, windowStart, windowEnd);
            var rows = ReadMessages(connection, tenantId, scope, windowStart, windowEnd);
            var truncated = scopeCount > MaxMessages;

            var parsed = new List<ParsedMessage>(rows.Count);
            var unparsable = 0;

            foreach (var row in rows)
            {
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

            var replyCandidates = scope.Kind is PropertyScopeKind.Entity or PropertyScopeKind.Company
                ? ReadMessages(
                    connection,
                    tenantId,
                    scope,
                    windowStart,
                    windowEnd.Add(ReplyWindow),
                    onlyOutbound: true)
                : [];

            var snapshot = Build(
                tenantId,
                scope,
                windowStart,
                windowEnd,
                parsed,
                unparsable,
                truncated,
                tenantMessages,
                hasher,
                replyCandidates);

            return Task.FromResult(snapshot);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The profile could not be read.", ex);
        }
    }

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
        List<MessageRow> replyCandidates)
    {
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

        var counts = new KpiCounts(parsed);
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
                tenantId, scope, parsed, replyCandidates, hasher,
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

    // ---------------------------------------------------------------------------------------------
    // KPI construction
    // ---------------------------------------------------------------------------------------------

    private static KpiValue BuildResponsiveness(
        string tenantId,
        PropertyScope scope,
        List<ParsedMessage> parsed,
        List<MessageRow> replyCandidates,
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

        var outbound = new List<(DateTimeOffset At, string Counterparty)>();

        foreach (var candidate in replyCandidates)
        {
            var assessment = Parse(candidate.Payload);
            if (assessment is null || candidate.Direction != MailDirection.Outbound)
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

        var matched = 0;
        var gaps = new List<double>();

        foreach (var message in inbound)
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

        var denominator = inbound.Count;
        var coverage = new KpiCoverage
        {
            Analysed = denominator,
            Total = total,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Truncated = truncated,
            Reason = denominator == 0
                ? KpiReasons.SignalNotProduced
                : denominator < definition.MinimumSample
                    ? KpiReasons.NotEnoughHistory
                    : null,
        };

        if (denominator == 0)
        {
            return new KpiValue
            {
                Definition = definition,
                State = KpiState.NotProduced,
                Coverage = coverage,
                Reason = KpiReasons.SignalNotProduced,
                Detail = "No inbound message carried a counterparty address to match.",
            };
        }

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
                Detail = $"Needs at least {definition.MinimumSample} inbound messages with an address; "
                    + $"saw {denominator}.",
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

        return new KpiValue
        {
            Definition = definition,
            State = KpiState.Produced,
            Value = (double)matched / denominator,
            Numerator = matched,
            Denominator = denominator,
            Coverage = coverage,
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
        public KpiCounts(List<ParsedMessage> messages)
        {
            InboundMessages = messages.Count(message => message.Row.Direction == MailDirection.Inbound);
            OutboundMessages = messages.Count(message => message.Row.Direction == MailDirection.Outbound);

            foreach (var message in messages)
            {
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
        => evidence.Availability is EvidenceAvailability.Available
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

    private static List<MessageRow> ReadMessages(
        SqliteConnection connection,
        string tenantId,
        PropertyScope scope,
        DateTimeOffset from,
        DateTimeOffset to,
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
        command.Parameters.AddWithValue("$limit", MaxMessages);

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
