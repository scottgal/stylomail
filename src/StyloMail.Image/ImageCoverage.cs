namespace StyloMail.Image;

/// <summary>
/// Per-message image coverage counts, including images deliberately skipped as chrome and images
/// not admitted because the budget was exhausted.
/// </summary>
public sealed record ImageCoverageSummary
{
    /// <summary>Image parts present in the message.</summary>
    public required int ImageParts { get; init; }

    /// <summary>Parts excluded as chrome (tracking pixels, tiny icons).</summary>
    public required int ImagesSkippedAsChrome { get; init; }

    /// <summary>Parts handed to the inspector. At most <see cref="ImageObservationContract.MaxAdmittedImages"/>.</summary>
    public required int ImagesAdmitted { get; init; }

    /// <summary>Admitted parts that produced structural facts (Inspected or Refused).</summary>
    public required int ImagesProduced { get; init; }

    /// <summary>Admitted parts that produced a named absence.</summary>
    public required int ImagesNotProduced { get; init; }

    /// <summary>Eligible parts not admitted because the per-message budget was exhausted.</summary>
    public required int ImagesOverBudget { get; init; }

    public int? FirstOverBudgetOrdinal { get; init; }

    public int? LastOverBudgetOrdinal { get; init; }

    /// <summary>
    /// Enforces the two invariants the aggregate row publishes. A summary that does not add up is a
    /// producer defect, not evidence, so it is refused rather than written to the ledger.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ImageParts);
        ArgumentOutOfRangeException.ThrowIfNegative(ImagesSkippedAsChrome);
        ArgumentOutOfRangeException.ThrowIfNegative(ImagesAdmitted);
        ArgumentOutOfRangeException.ThrowIfNegative(ImagesProduced);
        ArgumentOutOfRangeException.ThrowIfNegative(ImagesNotProduced);
        ArgumentOutOfRangeException.ThrowIfNegative(ImagesOverBudget);

        if (ImagesSkippedAsChrome + ImagesAdmitted + ImagesOverBudget != ImageParts)
        {
            throw new ArgumentException(
                "imageParts must equal imagesSkippedAsChrome + imagesAdmitted + imagesOverBudget.",
                nameof(ImageParts));
        }

        if (ImagesProduced + ImagesNotProduced != ImagesAdmitted)
        {
            throw new ArgumentException(
                "imagesAdmitted must equal imagesProduced + imagesNotProduced.",
                nameof(ImagesAdmitted));
        }

        if (ImagesOverBudget == 0)
        {
            if (FirstOverBudgetOrdinal is not null || LastOverBudgetOrdinal is not null)
            {
                throw new ArgumentException(
                    "Over-budget ordinals must be null when no image is over budget.",
                    nameof(FirstOverBudgetOrdinal));
            }

            return;
        }

        if (FirstOverBudgetOrdinal is null || LastOverBudgetOrdinal is null)
        {
            throw new ArgumentException(
                "Over-budget ordinals are required when imagesOverBudget is positive.",
                nameof(FirstOverBudgetOrdinal));
        }

        if (FirstOverBudgetOrdinal > LastOverBudgetOrdinal)
        {
            throw new ArgumentException(
                "FirstOverBudgetOrdinal must not be greater than LastOverBudgetOrdinal.",
                nameof(FirstOverBudgetOrdinal));
        }
    }
}
