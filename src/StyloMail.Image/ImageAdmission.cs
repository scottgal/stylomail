namespace StyloMail.Image;

/// <summary>
/// One image part offered for admission. Dimensions and transparency come from a bounded header
/// probe; they are null when the probe could not reach them, never guessed.
/// </summary>
public sealed record ImageCandidate
{
    /// <summary>Trusted part ordinal from the owning observation context. Unique and non-negative.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Decoded bytes available for this part.</summary>
    public required long DecodedBytes { get; init; }

    public long? Width { get; init; }

    public long? Height { get; init; }

    /// <summary>Header-level transparency declaration, when the format and probe established it.</summary>
    public bool? TransparencyDeclared { get; init; }

    /// <summary>True only when both dimensions are known.</summary>
    public bool HasKnownDimensions => Width.HasValue && Height.HasValue;
}

/// <summary>
/// Structural admission rules. The defaults are <see cref="ImageObservationContract"/> values.
/// </summary>
public sealed record ImageAdmissionPolicy
{
    public int MaxAdmittedImages { get; init; } = ImageObservationContract.MaxAdmittedImages;

    /// <summary>Minimum width and height when dimensions are known.</summary>
    public int MinDimension { get; init; } = ImageObservationContract.MinDimension;

    /// <summary>Minimum pixel count when dimensions are known.</summary>
    public long MinPixels { get; init; } = ImageObservationContract.MinPixels;

    /// <summary>Minimum width and height for a transparent image to remain eligible.</summary>
    public int TransparentMinDimension { get; init; } = ImageObservationContract.TransparentMinDimension;

    /// <summary>Minimum pixel count for a transparent image to remain eligible.</summary>
    public long TransparentMinPixels { get; init; } = ImageObservationContract.TransparentMinPixels;

    /// <summary>Byte floor applied only when dimensions are unknown. A small QR never meets this when its dimensions are known.</summary>
    public long UnknownDimensionMinBytes { get; init; } = ImageObservationContract.UnknownDimensionMinBytes;

    public int MaxProbes { get; init; } = ImageObservationContract.MaxProbes;

    public int ProbeBytes { get; init; } = ImageObservationContract.ProbeBytes;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAdmittedImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TransparentMinDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TransparentMinPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(UnknownDimensionMinBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxProbes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProbeBytes);

        if (ProbeBytes > ImageObservationContract.PerImageInspectedBytes)
        {
            throw new ArgumentException(
                "A probe cannot be larger than the per-image inspection prefix.",
                nameof(ProbeBytes));
        }
    }
}

/// <summary>
/// Deterministic admission result. Admitted candidates are in inspection priority order; the other
/// lists are in ascending trusted-part ordinal order.
/// </summary>
public sealed record ImageAdmissionResult
{
    public required IReadOnlyList<ImageCandidate> Admitted { get; init; }

    public required IReadOnlyList<int> SkippedChromeOrdinals { get; init; }

    public required IReadOnlyList<int> OverBudgetOrdinals { get; init; }

    public required int ImageParts { get; init; }

    public int ImagesAdmitted => Admitted.Count;

    public int ImagesSkippedAsChrome => SkippedChromeOrdinals.Count;

    public int ImagesOverBudget => OverBudgetOrdinals.Count;

    public int? FirstOverBudgetOrdinal => ImagesOverBudget == 0 ? null : OverBudgetOrdinals[0];

    public int? LastOverBudgetOrdinal => ImagesOverBudget == 0 ? null : OverBudgetOrdinals[^1];

    /// <summary>
    /// Builds the aggregate coverage summary once the admitted images have been inspected.
    /// </summary>
    public ImageCoverageSummary ToCoverageSummary(int imagesProduced, int imagesNotProduced)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(imagesProduced);
        ArgumentOutOfRangeException.ThrowIfNegative(imagesNotProduced);
        if (imagesProduced + imagesNotProduced != ImagesAdmitted)
        {
            throw new ArgumentException(
                "Produced plus not-produced must equal the number of admitted images.");
        }

        var summary = new ImageCoverageSummary
        {
            ImageParts = ImageParts,
            ImagesSkippedAsChrome = ImagesSkippedAsChrome,
            ImagesAdmitted = ImagesAdmitted,
            ImagesProduced = imagesProduced,
            ImagesNotProduced = imagesNotProduced,
            ImagesOverBudget = ImagesOverBudget,
            FirstOverBudgetOrdinal = FirstOverBudgetOrdinal,
            LastOverBudgetOrdinal = LastOverBudgetOrdinal,
        };

        summary.Validate();
        return summary;
    }
}

/// <summary>
/// Chooses which image parts to inspect, preferring content over chrome.
/// </summary>
/// <remarks>
/// The ordering is size descending with ties by trusted part ordinal, so the same parts always
/// produce the same admitted set. Chrome rules are applied first and counted separately from the
/// budget: a skipped tracking pixel did not consume an inspection slot.
/// </remarks>
public static class ImageAdmission
{
    public static ImageAdmissionResult Select(
        IEnumerable<ImageCandidate> candidates,
        ImageAdmissionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        policy ??= new ImageAdmissionPolicy();
        policy.Validate();

        var all = Materialise(candidates);
        var chrome = new List<int>();
        var eligible = new List<ImageCandidate>(all.Count);

        foreach (var candidate in all)
        {
            if (IsChrome(candidate, policy))
            {
                chrome.Add(candidate.Ordinal);
            }
            else
            {
                eligible.Add(candidate);
            }
        }

        var ranked = eligible
            .OrderByDescending(c => c.DecodedBytes)
            .ThenBy(c => c.Ordinal)
            .ToList();

        var admitted = ranked.Take(policy.MaxAdmittedImages).ToList();
        var overBudget = ranked
            .Skip(policy.MaxAdmittedImages)
            .Select(c => c.Ordinal)
            .OrderBy(ordinal => ordinal)
            .ToList();

        chrome.Sort();

        return new ImageAdmissionResult
        {
            Admitted = admitted,
            SkippedChromeOrdinals = chrome,
            OverBudgetOrdinals = overBudget,
            ImageParts = all.Count,
        };
    }

    /// <summary>
    /// Ordinals that need a bounded header probe before admission, largest first, at most
    /// <see cref="ImageAdmissionPolicy.MaxProbes"/>. Probes are only for candidates whose
    /// dimensions are still unknown.
    /// </summary>
    public static IReadOnlyList<int> SelectProbeOrdinals(
        IEnumerable<ImageCandidate> candidates,
        ImageAdmissionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        policy ??= new ImageAdmissionPolicy();
        policy.Validate();

        return Materialise(candidates)
            .Where(c => c.DecodedBytes > 0 && !c.HasKnownDimensions)
            .OrderByDescending(c => c.DecodedBytes)
            .ThenBy(c => c.Ordinal)
            .Take(policy.MaxProbes)
            .Select(c => c.Ordinal)
            .ToList();
    }

    private static bool IsChrome(ImageCandidate candidate, ImageAdmissionPolicy policy)
    {
        if (candidate.DecodedBytes <= 0)
        {
            return true;
        }

        if (candidate.Width is { } width && candidate.Height is { } height)
        {
            var pixels = Pixels(width, height);

            // F2: tiny dimensions are chrome regardless of transparency.
            if (width < policy.MinDimension ||
                height < policy.MinDimension ||
                pixels < policy.MinPixels)
            {
                return true;
            }

            // F3: transparent and small is the tracking-pixel class. A full-size transparent
            // overlay stays eligible.
            if (candidate.TransparencyDeclared == true &&
                (width < policy.TransparentMinDimension ||
                 height < policy.TransparentMinDimension ||
                 pixels < policy.TransparentMinPixels))
            {
                return true;
            }

            return false;
        }

        // F1 applies only when the probe could not reach a header. A known-dimension small QR is
        // never excluded for being small in bytes.
        return candidate.DecodedBytes < policy.UnknownDimensionMinBytes;
    }

    private static long Pixels(long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        return width > long.MaxValue / height ? long.MaxValue : width * height;
    }

    private static List<ImageCandidate> Materialise(IEnumerable<ImageCandidate> candidates)
    {
        var all = new List<ImageCandidate>();
        var ordinals = new HashSet<int>();

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentOutOfRangeException.ThrowIfNegative(candidate.Ordinal);
            ArgumentOutOfRangeException.ThrowIfNegative(candidate.DecodedBytes);

            if (!ordinals.Add(candidate.Ordinal))
            {
                throw new ArgumentException(
                    $"Duplicate image candidate ordinal {candidate.Ordinal}.",
                    nameof(candidates));
            }

            all.Add(candidate);
        }

        return all;
    }
}
