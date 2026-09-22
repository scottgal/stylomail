namespace StyloMail.Adaptive.Temporal;

/// <summary>
/// Turns movements into a sentence an operator can act on.
/// </summary>
/// <remarks>
/// "Acceleration 0.31" tells a reviewer nothing they can check. "Recipient fan-out rising while
/// payment-redirection evidence also rises" tells them what to look at, and it is the difference
/// between an explainable decision and a number with a label attached.
/// </remarks>
public static class TrendNarrative
{
    /// <summary>Phrase used for the fan-out rate feature.</summary>
    public const string FanOutLabel = "recipient fan-out";

    /// <summary>Phrase used for the message-rate feature.</summary>
    public const string MessageRateLabel = "message rate";

    public static bool IsRateFeature(string dimensionId) =>
        dimensionId.StartsWith("rate.", StringComparison.Ordinal);

    public static string LabelFor(string dimensionId) => dimensionId switch
    {
        FeatureIds.RecipientsPerSecond => FanOutLabel,
        FeatureIds.MessagesPerSecond => MessageRateLabel,
        _ when dimensionId.StartsWith("semantic.", StringComparison.Ordinal) =>
            $"{dimensionId["semantic.".Length..].Replace('_', '-')} evidence",
        _ => dimensionId.Replace('_', '-'),
    };

    public static string Describe(IReadOnlyList<TrendMovement> movements, string windowName)
    {
        ArgumentNullException.ThrowIfNull(movements);

        var labels = movements.Select(movement => movement.Label).ToArray();

        return labels.Length switch
        {
            0 => $"no material movement in the {windowName} window",
            1 => $"{labels[0]} rising",
            2 => $"{labels[0]} rising while {labels[1]} also rises",
            _ => $"{labels[0]} rising while {string.Join(", ", labels[1..^1])} and {labels[^1]} also rise",
        };
    }
}
