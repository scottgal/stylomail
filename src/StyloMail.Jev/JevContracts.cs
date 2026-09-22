using System.Text.Json.Serialization;

namespace StyloMail.Jev;

/// <summary>
/// Wire DTOs for <c>POST /v1/systemone</c>.
/// </summary>
/// <remarks>
/// These mirror the published TypeSafe API reference as verified on 2026-09-22. They are
/// deliberately internal: provider-specific shapes must not leak into Core, and a future
/// compatible local classifier must not inherit TypeSafe's field names.
/// </remarks>
internal sealed record JevRequest
{
    [JsonPropertyName("state")]
    public required object State { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("questions")]
    public required IReadOnlyDictionary<string, JevQuestion> Questions { get; init; }
}

internal sealed record JevQuestion
{
    /// <summary>Always <c>noul</c> here: the semantic dimensions are independent properties.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// The complete question. A question key is not sent to the model and is not used in
    /// inference, so the full judgment must live here.
    /// </summary>
    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }

    [JsonPropertyName("criteria")]
    public JevNoulCriteria? Criteria { get; init; }
}

internal sealed record JevNoulCriteria
{
    [JsonPropertyName("true")]
    public required string True { get; init; }

    [JsonPropertyName("false")]
    public required string False { get; init; }
}

internal sealed record JevResponse
{
    /// <summary>Resolved model id, e.g. <c>jev-1.13.0</c>. Recorded on every assessment.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("answers")]
    public Dictionary<string, JevAnswer>? Answers { get; init; }

    [JsonPropertyName("usage")]
    public JevUsage? Usage { get; init; }
}

internal sealed record JevAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Probability the answer is yes, 0..1. Near 0.5 means balanced, not "moderate".</summary>
    [JsonPropertyName("noul")]
    public double? Noul { get; init; }

    [JsonPropertyName("choice")]
    public string? Choice { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; init; }

    /// <summary>
    /// Present for Choice and Score answers only. <b>Noul answers never carry it</b>, which is why
    /// the corresponding <c>Evidence.Confidence</c> is explicitly null rather than defaulted.
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }
}

internal sealed record JevUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}
