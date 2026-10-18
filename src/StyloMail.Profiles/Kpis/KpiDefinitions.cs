namespace StyloMail.Profiles.Kpis;

/// <summary>
/// The versioned KPI definitions this build serves, and the exact claims each one makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six agreed KPIs, seven definitions.</b> Responsiveness is one question with a rate and a
/// latency, so the latency travels as a measurement beside the rate rather than becoming a separate
/// KPI with a denominator that does not describe it.
/// </para>
/// <para>
/// <b>Every definition names what it does not measure.</b> The spec forbids composite trust,
/// friendliness and manipulativeness scores; tone and friendliness are not measured until
/// calibrated, and true reply threading is not recorded at all. A definition that stayed silent
/// about those would let a reader assume the tile covers them.
/// </para>
/// </remarks>
public static class KpiDefinitions
{
    public const string Version = "1.0.0";

    public const string Responsiveness = "responsiveness.address_reciprocity";
    public const string Momentum = "momentum.volume_change";
    public const string Pressure = "pressure.urgency_prevalence";
    public const string SensitiveRequests = "sensitive_requests.model_prevalence";
    public const string Consistency = "consistency.identity_anomaly_prevalence";
    public const string Handling = "handling.intervention_rate";

    public static readonly IReadOnlyList<KpiDefinition> All =
    [
        new()
        {
            Id = Responsiveness,
            Group = "Responsiveness",
            Label = "Responded from the same address within 7 days",

            // "Replied" is deliberately absent. The proxy matches addresses, not messages, and a
            // tile that said "replied" would claim a thread linkage the ledger does not record.
            Question = "Does this relationship respond, and how quickly?",
            Numerator = "Inbound messages that were followed within 7 days by an outbound message "
                + "from this scope to the same counterparty address, matched by a tenant-keyed HMAC "
                + "of the normalised address.",
            Denominator = "Inbound messages that carried a counterparty address and whose 7-day "
                + "reply window has fully elapsed.",
            Window = "Inbound messages in the profile window whose 7-day reply window has fully "
                + "elapsed; each is searched forward up to 7 days for an outbound match. Recent "
                + "inbound messages are excluded and counted, not treated as unanswered. The "
                + "outbound candidate set is capped; when it is capped or unreadable the value is "
                + "NotProduced rather than undercounted. Matches are by address reciprocity only: "
                + "true In-Reply-To or thread linkage is NOT produced.",
            MinimumSample = 5,
            Direction = KpiDirection.HigherIsBetter,
            Unit = KpiUnit.Ratio,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
                KpiReasons.NotEnoughHistory,
                KpiReasons.NoProfileKey,
                KpiReasons.NoAddressReciprocityInScope,
                KpiReasons.NoOutboundRecipientIdentity,
                KpiReasons.ReplyCandidatesIncomplete,
                KpiReasons.NoFullyObservedInbound,
            ],
            Version = Version,
        },
        new()
        {
            Id = Momentum,
            Group = "Momentum",
            Label = "Message volume against its own prior window",
            Question = "Is the relationship active or fading?",
            Numerator = "Attributable messages in the recent half of the profile window.",
            Denominator = "Attributable messages in the prior half of the same window.",
            Window = "The profile window split into two equal halves. The ratio has no meaning "
                + "against another entity's traffic; it is this relationship against its own past.",
            MinimumSample = 3,
            Direction = KpiDirection.Neutral,
            Unit = KpiUnit.Multiple,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
                KpiReasons.NotEnoughHistory,
            ],
            Version = Version,
        },
        new()
        {
            Id = Pressure,
            Group = "Pressure",
            Label = "Pressure language prevalence",
            Question = "Is pressure language present, and how often?",
            Numerator = "Messages where semantic.urgency_pressure was answered and scored at or "
                + "above 0.5.",
            Denominator = "Messages where semantic.urgency_pressure was answered. Provider silence "
                + "is excluded from the denominator, so an outage cannot read as zero prevalence.",
            Window = "Attributable messages in the profile window.",
            MinimumSample = 5,
            Direction = KpiDirection.Neutral,
            Unit = KpiUnit.Ratio,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
                KpiReasons.SignalNotProduced,
                KpiReasons.NotEnoughHistory,
            ],
            Version = Version,
        },
        new()
        {
            Id = SensitiveRequests,
            Group = "Sensitive requests",
            Label = "Sensitive request prevalence (model evidence)",
            Question = "Have credentials, bank details or payment changes been requested?",
            Numerator = "Messages where semantic.credential_request or "
                + "semantic.payment_redirection was answered and scored at or above 0.5.",
            Denominator = "Messages where at least one of those two dimensions was answered.",
            Window = "Attributable messages in the profile window. This is model evidence, not a "
                + "deterministic request detector: the repository has no deterministic credential or "
                + "payment-request signal, and the definition names the source rather than claiming "
                + "one.",
            MinimumSample = 5,
            Direction = KpiDirection.Neutral,
            Unit = KpiUnit.Ratio,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
                KpiReasons.SignalNotProduced,
                KpiReasons.NotEnoughHistory,
            ],
            Version = Version,
        },
        new()
        {
            Id = Consistency,
            Group = "Consistency",
            Label = "Identity anomaly prevalence per message",
            Question = "Has identity stayed stable?",
            Numerator = "Messages with at least one deterministic identity anomaly: "
                + "deterministic.display_name_address_mismatch, deterministic.envelope_header_identity, "
                + "deterministic.reply_to_divergence or deterministic.thread_header_consistency.",
            Denominator = "Messages where at least one of those deterministic identity signals was "
                + "produced.",
            Window = "Attributable messages in the profile window. This is per-message anomaly "
                + "prevalence, NOT drift over time: display-name and signature drift are not "
                + "captured and are reported as not produced.",
            MinimumSample = 5,
            Direction = KpiDirection.LowerIsBetter,
            Unit = KpiUnit.Ratio,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
                KpiReasons.SignalNotProduced,
                KpiReasons.NotEnoughHistory,
            ],
            Version = Version,
        },
        new()
        {
            Id = Handling,
            Group = "Handling",
            Label = "Share of messages policy intervened on",
            Question = "How often has policy intervened?",
            Numerator = "Messages whose decision was Hold, Quarantine or Review, or which carried a "
                + "held, quarantined or review recipient disposition.",
            Denominator = "Attributable messages in the profile window.",
            Window = "Attributable messages in the profile window. Zero is a measurement here, "
                + "because the denominator exists whenever the population is non-empty.",
            MinimumSample = 1,
            Direction = KpiDirection.Neutral,
            Unit = KpiUnit.Ratio,
            AvailabilityReasons =
            [
                KpiReasons.NoAttributableMessages,
            ],
            Version = Version,
        },
    ];

    private static readonly Dictionary<string, KpiDefinition> ById =
        All.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static bool TryGet(string? id, out KpiDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    public static KpiDefinition Require(string id)
        => TryGet(id, out var definition)
            ? definition
            : throw new ArgumentException($"'{id}' is not a KPI this build defines.", nameof(id));
}
