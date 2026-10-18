namespace StyloMail.Profiles.Properties;

/// <summary>
/// A request to set one property at one scope.
/// </summary>
/// <remarks>
/// <b><see cref="Actor"/> is supplied by the caller from the authenticated principal, never from a
/// request body.</b> An audit stamp a caller can set is not an audit stamp. The same applies to a
/// declared country written by an import: the import principal is the actor, and the reason names
/// the system it came from, so jurisdiction always traces to someone or something accountable.
/// </remarks>
public sealed record PropertyWrite
{
    public required PropertyScope Scope { get; init; }

    public required string Key { get; init; }

    public required string Value { get; init; }

    public required PropertySource Source { get; init; }

    public required string Actor { get; init; }

    public double? Confidence { get; init; }

    public ModelContribution? Model { get; init; }

    public IReadOnlyList<EvidenceReference> EvidenceRefs { get; init; } = [];

    /// <summary>The denominator behind <see cref="Confidence"/>, when the source has one.</summary>
    public PropertyCoverage? Coverage { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The outcome of checking a write, including the value that should be stored.</summary>
public sealed record PropertyWriteCheck
{
    public required bool Accepted { get; init; }

    /// <summary>The normalised value to store. Null when refused.</summary>
    public string? NormalisedValue { get; init; }

    /// <summary>A machine-readable refusal code, stable enough for a client to branch on.</summary>
    public string? ErrorCode { get; init; }

    public string? Detail { get; init; }

    public static PropertyWriteCheck Refused(string code, string detail)
        => new() { Accepted = false, ErrorCode = code, Detail = detail };
}

/// <summary>
/// The rules a property write must satisfy before it may be stored.
/// </summary>
/// <remarks>
/// <para>
/// These live with the model rather than in the endpoint so that every writer, not just HTTP, gets
/// the same answer. The endpoint still decides <em>authorisation</em> (who may write at all); this
/// decides whether the value is one the system can honestly hold.
/// </para>
/// <para>
/// <b>Inference is refused by default.</b> Spec §9.16 makes age inference off unless a deployment
/// is mixed or unknown-audience, so an inferred write needs the deployment to say so. The check
/// takes that as a parameter rather than reading configuration, because a library that reads
/// configuration is a library with a second, invisible composition root.
/// </para>
/// </remarks>
public static class PropertyWriteRules
{
    /// <summary>How many evidence references one row may carry.</summary>
    public const int MaxEvidenceRefs = 32;

    /// <summary>How long one evidence reference or span may be.</summary>
    public const int MaxEvidenceRefLength = 256;

    /// <summary>How long an evidence label may be. A label names the item; it is not its content.</summary>
    public const int MaxEvidenceLabelLength = 200;

    /// <summary>How many spans one model contribution may name.</summary>
    public const int MaxSpans = 16;

    /// <summary>How long a stated reason may be.</summary>
    public const int MaxReasonLength = 512;

    public static PropertyWriteCheck Check(PropertyWrite write, bool inferenceEnabled, DateTimeOffset now)
    {
        if (!PropertyKeys.TryGet(write.Key, out var definition))
        {
            return PropertyWriteCheck.Refused(
                "unknown_property_key",
                $"'{write.Key}' is not a property key this build understands.");
        }

        if (string.IsNullOrWhiteSpace(write.Actor))
        {
            return PropertyWriteCheck.Refused(
                "actor_required",
                "A property change must name the principal that made it.");
        }

        if (string.IsNullOrWhiteSpace(write.Scope.Id))
        {
            return PropertyWriteCheck.Refused("scope_required", "A property change must name its scope.");
        }

        if (!PropertyKeys.AcceptsSource(definition, write.Source))
        {
            return PropertyWriteCheck.Refused(
                "source_not_permitted",
                $"{definition.Label} may be {Describe(definition.AllowedSources)}, not "
                + $"{write.Source.ToString().ToLowerInvariant()}.");
        }

        if (write.Source == PropertySource.Inferred && !inferenceEnabled)
        {
            return PropertyWriteCheck.Refused(
                "inference_not_enabled",
                "Inference is disabled for this deployment, so an inferred value cannot be stored. "
                + "Enable StyloMail:Properties:AllowInferredWrites only for a deployment whose "
                + "audience is mixed or unknown.");
        }

        if (!PropertyKeys.TryNormalise(definition, write.Value, out var normalised, out var reason))
        {
            return PropertyWriteCheck.Refused("invalid_property_value", reason!);
        }

        if (write.ExpiresAt is { } expiry && expiry <= now)
        {
            return PropertyWriteCheck.Refused(
                "expiry_not_in_future",
                "An expiry at or before now would store a value that is already unknown. Clear the "
                + "property instead, so the change is recorded as a clear rather than as a set.");
        }

        if (write.Confidence is { } confidence && (confidence < 0 || confidence > 1))
        {
            return PropertyWriteCheck.Refused(
                "confidence_out_of_range",
                "Confidence is a fraction between 0 and 1.");
        }

        if (write.Source == PropertySource.Inferred)
        {
            var model = write.Model;
            if (model is null
                || string.IsNullOrWhiteSpace(model.Id)
                || string.IsNullOrWhiteSpace(model.Version)
                || string.IsNullOrWhiteSpace(model.PromptContract))
            {
                return PropertyWriteCheck.Refused(
                    "model_contribution_required",
                    "An inferred value must name its model, version and prompt contract. A value "
                    + "nobody can reproduce is a value nobody can defend.");
            }

            if (write.Confidence is null)
            {
                return PropertyWriteCheck.Refused(
                    "confidence_required",
                    "An inferred value must carry its confidence.");
            }
        }

        if (write.EvidenceRefs.Count > MaxEvidenceRefs)
        {
            return PropertyWriteCheck.Refused(
                "too_many_evidence_refs",
                $"A property may cite at most {MaxEvidenceRefs} evidence references.");
        }

        foreach (var reference in write.EvidenceRefs)
        {
            if (string.IsNullOrWhiteSpace(reference.Id)
                || reference.Id.Length > MaxEvidenceRefLength)
            {
                return PropertyWriteCheck.Refused(
                    "invalid_evidence_ref",
                    $"An evidence reference must have a non-empty id of at most "
                    + $"{MaxEvidenceRefLength} characters.");
            }

            if (reference.Label is { Length: > MaxEvidenceLabelLength })
            {
                return PropertyWriteCheck.Refused(
                    "invalid_evidence_label",
                    $"An evidence label may be at most {MaxEvidenceLabelLength} characters. "
                    + "It is a short name for the item, never its content.");
            }

            if (reference.RetentionUntil is { } until
                && reference.ObservedAt is { } observed
                && until < observed)
            {
                return PropertyWriteCheck.Refused(
                    "invalid_retention",
                    "Retention cannot end before the evidence was observed.");
            }
        }

        if (write.Coverage is { } coverage)
        {
            if (write.Source is not (PropertySource.Inferred or PropertySource.Import))
            {
                return PropertyWriteCheck.Refused(
                    "coverage_not_applicable",
                    "Only a model inference or an import has a sample to state. A declared, verified "
                    + "or default value has no denominator and must not imply one.");
            }

            if (coverage.UnavailableReason is { Length: > MaxReasonLength })
            {
                return PropertyWriteCheck.Refused(
                    "coverage_reason_too_long",
                    $"A coverage reason may be at most {MaxReasonLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(coverage.UnavailableReason))
            {
                if (coverage.Analysed is null || coverage.Total is null
                    || coverage.WindowStart is null || coverage.WindowEnd is null)
                {
                    return PropertyWriteCheck.Refused(
                        "coverage_incomplete",
                        "Coverage must state analysed count, total count and both window bounds, "
                        + "or a named reason it cannot.");
                }

                if (coverage.Analysed < 0 || coverage.Total < coverage.Analysed)
                {
                    return PropertyWriteCheck.Refused(
                        "coverage_invalid",
                        "Coverage counts must be non-negative and the analysed count cannot exceed "
                        + "the total.");
                }

                if (coverage.WindowStart > coverage.WindowEnd)
                {
                    return PropertyWriteCheck.Refused(
                        "coverage_invalid",
                        "The coverage window must not end before it starts.");
                }
            }
        }
        else if (write.Source == PropertySource.Inferred)
        {
            return PropertyWriteCheck.Refused(
                "coverage_required",
                "An inferred value must state the coverage its confidence rests on, or a named "
                + "reason coverage is unavailable. A confidence without a denominator is not enough.");
        }

        if (write.Model is { } contribution)
        {
            if (contribution.Spans.Count > MaxSpans)
            {
                return PropertyWriteCheck.Refused(
                    "too_many_spans",
                    $"A model contribution may name at most {MaxSpans} spans.");
            }

            var overlongSpan = contribution.Spans.FirstOrDefault(
                span => string.IsNullOrWhiteSpace(span) || span.Length > MaxEvidenceRefLength);
            if (overlongSpan is not null)
            {
                return PropertyWriteCheck.Refused(
                    "invalid_span",
                    $"A span must be non-empty and at most {MaxEvidenceRefLength} characters.");
            }
        }

        if (write.Reason is { Length: > MaxReasonLength })
        {
            return PropertyWriteCheck.Refused(
                "reason_too_long",
                $"A reason may be at most {MaxReasonLength} characters.");
        }

        return new PropertyWriteCheck { Accepted = true, NormalisedValue = normalised };
    }

    private static string Describe(IReadOnlyList<PropertySource> sources)
        => string.Join(
            " or ",
            sources.Select(source => source.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal));
}
