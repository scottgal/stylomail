# Embedding provider: the interface access- and proxyapi- consume

This is the consumer side of `ISegmentEmbeddingProvider` (`src/StyloMail.Embeddings/`). It exists so
a caller depends on a contract rather than on the loopback transport behind it.

## The interface

```csharp
public interface ISegmentEmbeddingProvider
{
    EmbeddingMetadata? Metadata { get; }
    Task<EmbeddingMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task<SegmentEmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default);
}
```

One call, one row per input segment, in input order:

```csharp
var batch = await provider.EmbedAsync(segments, cancellationToken);
foreach (var row in batch.Segments)
{
    if (row.IsAvailable)
    {
        // row.Vector is unit length, at provider.Metadata!.Dimension
    }
    else
    {
        // row.Reason.ToWireName() is one of the reasons below. There is no vector and no zero.
    }
}
```

`SegmentEmbedding.Ordinal` is the position in the request, so a row can be matched back to the
segment it came from.

## What the provider guarantees

1. **One row per segment, always.** A fault does not throw for an operational cause and does not
   return fewer rows. Rows are `NotProduced` with a named reason.
2. **No zero vector, ever.** A zero vector is a real point and would pull a centroid toward it.
   Absence is carried as `NotProduced`.
3. **A vector is either comparable or absent.** The provider refuses, rather than returns, a vector
   whose width is not the configured one, whose values are not finite, or whose norm is not 1 within
   1e-4. It also refuses the whole batch if the service's model tag, dimension or prefix version
   differ from configuration, or if it reports a dtype other than bfloat16 or float32 (`float16` is
   refused by name, because the model card says it degrades silently).
4. **The prefix is not yours to choose.** The service applies one prefix, versioned, to every
   segment. A caller-supplied prefix is rejected by the service.
5. **Bounded latency and no retry storm.** One call is bounded by `EmbeddingOptions.Timeout` (5 s by
   default). One retry, only for 503. A breaker opens after 5 consecutive failures for 30 s and
   reports `circuit_open`.
6. **The token is never in the options object.** `TokenFilePath` names a file; the provider reads it
   per call, so rotation is a file write and never a restart, and no configuration dump can render it.

## Configuration

`EmbeddingOptions`, all with usable defaults:

| Setting | Default | Notes |
| --- | --- | --- |
| `Endpoint` | `http://127.0.0.1:11440` | Loopback only. The service refuses any other bind. |
| `ModelTag` | `google/embeddinggemma-2` | Must equal the service's reported tag. |
| `Dimension` | `256` | One of 768, 512, 256, 128. Queries and prototypes must share it. |
| `PrefixVersion` | `classification/1` | Must equal the service's reported version. |
| `TokenFilePath` | `~/.config/stylomail/embedding-token` | Read per call, mode 0600. |
| `Timeout` | 5 s | Per HTTP call. |
| `MaxRetries` | 1 | 503 only. |
| `MaxSegments` | 32 | Also enforced by the service. |
| `MaxSegmentCharacters` | 6000 | Also enforced by the service. |
| `MetadataValidationInterval` | 15 min | How often the tag/dimension/prefix check is re-read. |

## Reasons, as they appear on a stored signal row

`service_unreachable`, `timed_out`, `unauthorized`, `token_unavailable`, `service_unavailable`,
`request_rejected`, `metadata_unavailable`, `model_tag_mismatch`, `dimension_mismatch`,
`prefix_version_mismatch`, `unsupported_dtype`, `contract_violation`, `non_finite_vector`,
`vector_not_normalised`, `too_many_segments`, `empty_segment`, `segment_too_long`, `circuit_open`.

`EmbeddingReasons.ToWireName()` is the single spelling of these; do not hand-write the strings.

## What this provider does not do

It does not segment a message. Segmentation is deterministic and bounded, and it belongs to the
caller that already holds the parsed message. It does not store vectors, does not compare them, and
does not decide anything. `EmbeddingMetadata` travels with the vectors so whatever stores them can
version the space they belong to.

## Wiring

Register one instance per Host process:

```csharp
var options = new EmbeddingOptions(); // or bound from configuration
var http = new HttpClient { BaseAddress = new Uri(options.Endpoint) };
var provider = new SegmentEmbeddingProvider(http, options, TimeProvider.System, logger);
```

The provider does not add a base address to the `HttpClient` it is given; it builds absolute URIs.
Passing a client with a base address is harmless. Do not set `HttpClient.Timeout` to something
shorter than `EmbeddingOptions.Timeout`; the provider's own deadline is the one it reports.
