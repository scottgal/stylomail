using System.Text.Json;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Core;
using StyloMail.Host.Serialization;
using StyloMail.Image;
using StyloMail.Mime;

namespace StyloMail.Host.Proxy;

/// <summary>
/// The real MIME image observer: probes dimensions, admits at most two images, inspects with one
/// serial 256 KiB lease and returns at most 13 Core evidence rows.
/// </summary>
internal sealed class ImagePartObserver : IImagePartObserver
{
    private static readonly ImageInspectionLimits ProbeLimits = new()
    {
        MaxInspectedBytes = 4096,
        MaxHeaderBytes = 4096,
        MaxHeaderSegments = 4096,
        MaxFindings = 16,
    };

    private static readonly ImageInspectionLimits CallSiteLimits = new()
    {
        MaxInspectedBytes = ImageObservationContract.PerImageInspectedBytes,
        MaxHeaderBytes = ImageObservationContract.PerImageInspectedBytes,
        MaxHeaderSegments = 4096,
        MaxFindings = 16,
    };

    private readonly ImageObservationIdentity _identity;
    private readonly string _tenantId;
    private readonly string _accountId;
    private readonly string _messageToken;
    private readonly string _threadToken;
    private readonly MessageTrafficKind _trafficKind;
    private readonly DateTimeOffset _observedAt;
    private readonly long _epoch;
    private readonly long _sequence;
    private readonly IImageScratchQuota _quota;
    private readonly ReadOnlyMemory<byte> _rawMessage;

    public ImagePartObserver(
        ImageObservationIdentity identity,
        string tenantId,
        string accountId,
        string messageToken,
        string threadToken,
        MessageTrafficKind trafficKind,
        DateTimeOffset observedAt,
        long epoch,
        long sequence,
        IImageScratchQuota quota,
        ReadOnlyMemory<byte> rawMessage)
    {
        _identity = identity;
        _tenantId = tenantId;
        _accountId = accountId;
        _messageToken = messageToken;
        _threadToken = threadToken;
        _trafficKind = trafficKind;
        _observedAt = observedAt;
        _epoch = epoch;
        _sequence = sequence;
        _quota = quota;
        _rawMessage = rawMessage;
    }

    /// <summary>The typed observation for the sink, or null when no observation was produced.</summary>
    public ProxyImageObservation? Observation { get; private set; }


    public IReadOnlyList<Evidence> Complete(IReadOnlyList<ImagePartSource> parts)
    {
        var lease = ImageScratchLease.TryRent(
            _quota, ImageObservationContract.PerImageInspectedBytes);

        if (!_identity.TryCreate(
                _tenantId, _accountId, _trafficKind.ToString(), _messageToken,
                _rawMessage.Span, out var key))
        {
            lease?.Dispose();
            return [];
        }

        try
        {
            var candidates = parts
                .Select(part => new Candidate(part))
                .ToList();

            if (lease is null)
            {
                return BuildQuotaDenied(candidates, key);
            }

            Probe(candidates, lease);

            var admission = ImageAdmission.Select(candidates.Select(candidate => candidate.ToCandidate()));
            var results = new List<(int Ordinal, ImageInspectionResult Result)>(admission.ImagesAdmitted);
            var partsByOrdinal = candidates.ToDictionary(candidate => candidate.Source.Ordinal);

            foreach (var candidate in admission.Admitted)
            {
                var source = partsByOrdinal[candidate.Ordinal].Source;
                results.Add((
                    candidate.Ordinal,
                    lease is null
                        ? ImageInspector.Inspect(new ImageInspectRequest
                        {
                            DeclaredContentType = source.DeclaredContentType,
                            Content = ReadOnlyMemory<byte>.Empty,
                            Extent = ImageContentExtent.PrefixTotalUnknown,
                        }, CallSiteLimits)
                        : Inspect(source, lease, CallSiteLimits, ImageObservationContract.PerImageInspectedBytes)));
            }

            var produced = results.Count(item => item.Result.Disposition != ImageDisposition.NotProduced);
            var notProduced = results.Count - produced;
            var evidence = new List<Evidence>();
            foreach (var item in results)
            {
                evidence.AddRange(ImageEvidence.Explain(item.Result, _observedAt));
            }

            var summary = admission.ToCoverageSummary(produced, notProduced);
            evidence.Add(ImageEvidence.ExplainCoverage(summary, _observedAt));

            var anyReduced = results.Any(item =>
                item.Result.Availability == EvidenceAvailability.ReducedCoverage);
            var availability = observationAvailability(
                admission.ImageParts, produced, admission.ImagesAdmitted,
                admission.ImagesOverBudget, anyReduced);
            var reason = availability switch
            {
                ProxyImageAvailability.Available => ProxyImageReason.None,
                ProxyImageAvailability.ReducedCoverage => ProxyImageReason.ImagesNotProduced,
                ProxyImageAvailability.NotApplicable => ProxyImageReason.None,
                _ => ProxyImageReason.ImagesNotProduced,
            };

            var provisional = new ProxyImageObservation
            {
                TenantId = _tenantId,
                AccountId = _accountId,
                MessageToken = _messageToken,
                ThreadToken = _threadToken,
                TrafficKind = _trafficKind,
                ObservationKey = key.ObservationKey,
                ContentFingerprint = key.ContentFingerprint,
                ObservationEpoch = _epoch,
                ObservationSequence = _sequence,
                ResultDigest = string.Empty,
                ObservedAt = _observedAt,
                Availability = availability,
                AvailabilityReason = reason,
                ImageParts = admission.ImageParts,
                ImagesSkippedAsChrome = admission.ImagesSkippedAsChrome,
                ImagesAdmitted = admission.ImagesAdmitted,
                ImagesProduced = produced,
                ImagesNotProduced = notProduced,
                ImagesOverBudget = admission.ImagesOverBudget,
                FirstOverBudgetOrdinal = admission.FirstOverBudgetOrdinal,
                LastOverBudgetOrdinal = admission.LastOverBudgetOrdinal,
                EvidenceRowCount = evidence.Count,
                Parts = [.. results.Select(item => MapPart(item.Result, item.Ordinal))],
                SourceVersion = ImageSignals.SourceVersion,
            };

            var canonical = JsonSerializer.SerializeToUtf8Bytes(
                provisional with
                {
                    ResultDigest = string.Empty,
                    ObservationEpoch = 0,
                    ObservationSequence = 0,
                    ObservedAt = default,
                },
                HostJson.Options);
            Observation = provisional with
            {
                ResultDigest = ImageObservationIdentity.ResultDigest(key, canonical),
            };
            return evidence;
        }
        catch (Exception)
        {
            Observation = null;
            return [];
        }
        finally
        {
            lease?.Dispose();
            key.Dispose();
        }
    }

    private static ProxyImageAvailability observationAvailability(
        int imageParts,
        int produced,
        int admitted,
        int overBudget,
        bool anyReducedCoverage)
    {
        if (imageParts == 0)
        {
            return ProxyImageAvailability.NotApplicable;
        }

        if (admitted == 0 || produced == 0)
        {
            return ProxyImageAvailability.Unavailable;
        }

        return produced == admitted && overBudget == 0 && !anyReducedCoverage
            ? ProxyImageAvailability.Available
            : ProxyImageAvailability.ReducedCoverage;
    }

    private IReadOnlyList<Evidence> BuildQuotaDenied(
        List<Candidate> candidates,
        ImageObservationKey key)
    {
        var ranked = candidates
            .OrderByDescending(candidate => candidate.Source.ObservedDecodedBytes)
            .ThenBy(candidate => candidate.Source.Ordinal)
            .ToList();
        var admitted = ranked.Take(2).ToList();
        var overBudget = ranked
            .Skip(2)
            .Select(candidate => candidate.Source.Ordinal)
            .OrderBy(ordinal => ordinal)
            .ToList();

        var availability = candidates.Count == 0
            ? ProxyImageAvailability.NotApplicable
            : ProxyImageAvailability.Unavailable;
        var reason = candidates.Count == 0 ? ProxyImageReason.None : ProxyImageReason.QuotaDenied;
        var summary = new ImageCoverageSummary
        {
            ImageParts = candidates.Count,
            ImagesSkippedAsChrome = 0,
            ImagesAdmitted = admitted.Count,
            ImagesProduced = 0,
            ImagesNotProduced = admitted.Count,
            ImagesOverBudget = overBudget.Count,
            FirstOverBudgetOrdinal = overBudget.Count == 0 ? null : overBudget[0],
            LastOverBudgetOrdinal = overBudget.Count == 0 ? null : overBudget[^1],
        };
        var evidence = new List<Evidence>
        {
            ImageEvidence.ExplainCoverage(summary, _observedAt),
        };

        var parts = admitted
            .Select(candidate => new ProxyImagePartObservation
            {
                Ordinal = candidate.Source.Ordinal,
                Disposition = ProxyImageDisposition.NotProduced,
                Availability = ProxyImageAvailability.Unavailable,
                AvailabilityReason = ProxyImageReason.QuotaDenied,
                Format = ImageFormat.Unknown.ToWireName(),
                DeclaredFormat = ImageFormatsFor(candidate.Source.DeclaredContentType),
                Findings = [],
                SourceVersion = ImageSignals.SourceVersion,
            })
            .ToArray();

        var provisional = new ProxyImageObservation
        {
            TenantId = _tenantId,
            AccountId = _accountId,
            MessageToken = _messageToken,
            ThreadToken = _threadToken,
            TrafficKind = _trafficKind,
            ObservationKey = key.ObservationKey,
            ContentFingerprint = key.ContentFingerprint,
            ObservationEpoch = _epoch,
            ObservationSequence = _sequence,
            ResultDigest = string.Empty,
            ObservedAt = _observedAt,
            Availability = availability,
            AvailabilityReason = reason,
            ImageParts = candidates.Count,
            ImagesSkippedAsChrome = 0,
            ImagesAdmitted = admitted.Count,
            ImagesProduced = 0,
            ImagesNotProduced = admitted.Count,
            ImagesOverBudget = overBudget.Count,
            FirstOverBudgetOrdinal = overBudget.Count == 0 ? null : overBudget[0],
            LastOverBudgetOrdinal = overBudget.Count == 0 ? null : overBudget[^1],
            EvidenceRowCount = evidence.Count,
            Parts = parts,
            SourceVersion = ImageSignals.SourceVersion,
        };
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            provisional with
            {
                ResultDigest = string.Empty,
                ObservationEpoch = 0,
                ObservationSequence = 0,
                ObservedAt = default,
            },
            HostJson.Options);
        Observation = provisional with
        {
            ResultDigest = ImageObservationIdentity.ResultDigest(key, canonical),
        };
        return evidence;
    }

    private static string? ImageFormatsFor(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';');
        var declared = separator >= 0 ? contentType[..separator] : contentType;
        return declared.Trim().ToLowerInvariant() switch
        {
            "image/png" => ImageFormat.Png.ToWireName(),
            "image/jpeg" or "image/jpg" => ImageFormat.Jpeg.ToWireName(),
            "image/gif" => ImageFormat.Gif.ToWireName(),
            "image/bmp" => ImageFormat.Bmp.ToWireName(),
            "image/webp" => ImageFormat.WebP.ToWireName(),
            _ => null,
        };
    }

    private static void Probe(List<Candidate> candidates, ImageScratchLease lease)
    {
        var ordinals = ImageAdmission.SelectProbeOrdinals(
            candidates.Select(candidate => candidate.ToCandidate()));
        foreach (var ordinal in ordinals)
        {
            var candidate = candidates.First(item => item.Source.Ordinal == ordinal);
            if (!TryRead(candidate.Source, lease, 4096, out var read, out var extent, out var total))
            {
                continue;
            }

            var result = ImageInspector.Inspect(
                new ImageInspectRequest
                {
                    DeclaredContentType = candidate.Source.DeclaredContentType,
                    Content = read == 0 ? ReadOnlyMemory<byte>.Empty : lease.Buffer[..read],
                    Extent = read == 0 ? ImageContentExtent.PrefixTotalUnknown : extent,
                    TotalLength = read == 0 ? null : total,
                },
                ProbeLimits);

            candidate.Width = result.Width;
            candidate.Height = result.Height;
            candidate.TransparencyDeclared = result.TransparencyDeclared;
        }
    }

    private static ImageInspectionResult Inspect(
        ImagePartSource source,
        ImageScratchLease lease,
        ImageInspectionLimits limits,
        int cap)
    {
        if (!TryRead(source, lease, cap, out var read, out var extent, out var total) || read == 0)
        {
            return ImageInspector.Inspect(new ImageInspectRequest
            {
                DeclaredContentType = source.DeclaredContentType,
                Content = ReadOnlyMemory<byte>.Empty,
                Extent = ImageContentExtent.PrefixTotalUnknown,
            }, limits);
        }

        return ImageInspector.Inspect(
            new ImageInspectRequest
            {
                DeclaredContentType = source.DeclaredContentType,
                Content = lease.Buffer[..read],
                Extent = extent,
                TotalLength = total,
            },
            limits);
    }

    private static bool TryRead(
        ImagePartSource source,
        ImageScratchLease lease,
        int cap,
        out int read,
        out ImageContentExtent extent,
        out long? totalLength)
    {
        read = 0;
        extent = ImageContentExtent.PrefixTotalUnknown;
        totalLength = null;
        if (!source.TryOpenContent(out var stream) || stream is null)
        {
            return false;
        }

        using (stream)
        {
            var buffer = lease.Buffer.Span;
            while (read < cap)
            {
                var count = stream.Read(buffer[read..cap]);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }
        }

        if (!source.DecodedLengthIsComplete || source.KnownDecodedLength is not { } known)
        {
            return true;
        }

        if (read >= known)
        {
            extent = ImageContentExtent.Complete;
            totalLength = null;
            return true;
        }

        totalLength = known;

        if (read == cap && known > cap)
        {
            extent = ImageContentExtent.PrefixTotalKnown;
            return true;
        }

        // EOF before a trusted known length: the total is not trustworthy.
        totalLength = null;
        extent = ImageContentExtent.PrefixTotalUnknown;
        return true;
    }

    private static ProxyImagePartObservation MapPart(ImageInspectionResult result, int ordinal) => new()
    {
        Ordinal = ordinal,
        Disposition = result.Disposition switch
        {
            ImageDisposition.Inspected => ProxyImageDisposition.Inspected,
            ImageDisposition.Refused => ProxyImageDisposition.Refused,
            _ => ProxyImageDisposition.NotProduced,
        },
        Availability = result.Availability switch
        {
            EvidenceAvailability.Available => ProxyImageAvailability.Available,
            EvidenceAvailability.ReducedCoverage => ProxyImageAvailability.ReducedCoverage,
            EvidenceAvailability.Unavailable => ProxyImageAvailability.Unavailable,
            _ => ProxyImageAvailability.NotApplicable,
        },
        AvailabilityReason = result.NotProducedReason switch
        {
            ImageNotProducedReason.EmptyContent => ProxyImageReason.EmptyContent,
            ImageNotProducedReason.NotAnImage => ProxyImageReason.NotAnImage,
            ImageNotProducedReason.UnsupportedFormat => ProxyImageReason.UnsupportedFormat,
            ImageNotProducedReason.FormatMismatch => ProxyImageReason.FormatMismatch,
            ImageNotProducedReason.TruncatedHeader => ProxyImageReason.TruncatedHeader,
            ImageNotProducedReason.HeaderBudgetExceeded => ProxyImageReason.HeaderBudgetExceeded,
            ImageNotProducedReason.InvalidDimensions => ProxyImageReason.InvalidDimensions,
            ImageNotProducedReason.InvalidStructure => ProxyImageReason.InvalidStructure,
            ImageNotProducedReason.InsufficientDecodedLength => ProxyImageReason.InsufficientDecodedLength,
            ImageNotProducedReason.InspectionBudgetExhausted => ProxyImageReason.InspectionBudgetExhausted,
            null => ProxyImageReason.None,
            _ => ProxyImageReason.InvalidStructure,
        },
        Format = result.DetectedFormat.ToWireName(),
        DeclaredFormat = result.DeclaredFormat.ToWireName(),
        Width = result.Width,
        Height = result.Height,
        PixelCount = result.PixelCount,
        ImageEndOffset = result.ImageEndOffset,
        TrailingByteCount = result.TrailingByteCount,
        TrailingBytesObserved = result.TrailingBytesObserved,
        TrailingScanComplete = result.TrailingScanComplete,
        TransparencyDeclared = result.TransparencyDeclared,
        Findings = [.. result.Findings.Select(finding => new ProxyImageFinding
        {
            Kind = finding.Code switch
            {
                ImageFindingCode.TypeMagicMismatch => ProxyImageFindingKind.TypeMagicMismatch,
                ImageFindingCode.TrailingData => ProxyImageFindingKind.TrailingData,
                ImageFindingCode.EmbeddedArchive => ProxyImageFindingKind.EmbeddedArchive,
                ImageFindingCode.EmbeddedExecutable => ProxyImageFindingKind.EmbeddedExecutable,
                ImageFindingCode.ScriptableFormat => ProxyImageFindingKind.ScriptableFormat,
                ImageFindingCode.ZeroDimensions => ProxyImageFindingKind.ZeroDimensions,
                ImageFindingCode.DimensionLimitExceeded => ProxyImageFindingKind.DimensionLimitExceeded,
                _ => ProxyImageFindingKind.PixelLimitExceeded,
            },
            Offset = finding.Offset,
            Detail = finding.Detail,
        })],
        SourceVersion = ImageSignals.SourceVersion,
    };

    private sealed class Candidate
    {
        public Candidate(ImagePartSource source)
        {
            Source = source;
        }

        public ImagePartSource Source { get; }

        public long? Width { get; set; }

        public long? Height { get; set; }

        public bool? TransparencyDeclared { get; set; }

        public ImageCandidate ToCandidate() => new()
        {
            Ordinal = Source.Ordinal,
            DecodedBytes = Source.ObservedDecodedBytes,
            Width = Width,
            Height = Height,
            TransparencyDeclared = TransparencyDeclared,
        };
    }
}
