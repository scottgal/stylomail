using MimeKit;
using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>
/// Observes the image parts found during one MIME walk.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call per analysis, after the walk.</b> The observer receives every collected image source
/// at once, performs any ranking, probing and inspection synchronously, and returns at most 13
/// Core evidence rows. Batching the call means no source has to survive a callback boundary.
/// </para>
/// <para>
/// <b>Bounded retention.</b> During <see cref="Complete"/> the observer may retain the small
/// descriptor facts on each source (ordinal, declared type, byte counts, dimensions it derives) but
/// it must not retain a source, a <see cref="Stream"/> or any other MIME entity after the call
/// returns. Open one stream, read it, and dispose it before opening the next. The scope invalidates
/// every source after the walk, and the parsed message is disposed deterministically by the
/// analyzer.
/// </para>
/// <para>
/// <b>Failure is not analysis failure.</b> An observer that throws, returns an oversized list, or
/// returns rows that do not belong to the deterministic image vocabulary contributes nothing; the
/// baseline MIME evidence is preserved. The observer must not throw, but the analyzer enforces that
/// outcome either way.
/// </para>
/// <para>
/// A fresh observer instance is expected per analysis. Shared mutable observer state would make
/// concurrent analyses interfere.
/// </para>
/// </remarks>
public interface IImagePartObserver
{
    /// <summary>
    /// Inspects the collected image sources and returns at most 13 Core evidence rows.
    /// </summary>
    /// <param name="parts">Image sources in trusted part-walk order. Valid only for this call.</param>
    IReadOnlyList<Evidence> Complete(IReadOnlyList<ImagePartSource> parts);
}

/// <summary>
/// One image part offered to an <see cref="IImagePartObserver"/>.
/// </summary>
/// <remarks>
/// The source carries no filename, subject or address. Its content stream is the decoded part
/// content, opened afresh on each call. Do not seek or inspect <see cref="Stream.CanSeek"/>,
/// <see cref="Stream.Position"/> or <see cref="Stream.Length"/> on the opened stream: MimeKit
/// returns a non-seekable decoder whose underlying stream is rewound by <c>Content.Open()</c>. The
/// first byte of the stream belongs to the consumer; nothing here reads or discards it.
/// </remarks>
public sealed class ImagePartSource
{
    private readonly CollectedImagePart _collected;
    private readonly ImagePartScope _scope;

    internal ImagePartSource(CollectedImagePart collected, ImagePartScope scope)
    {
        _collected = collected;
        _scope = scope;
    }

    /// <summary>Trusted 1-based part-walk ordinal. Not a new or guessed identifier.</summary>
    public int Ordinal => _collected.Ordinal;

    /// <summary>Declared content type exactly as the MIME part carried it. No filename.</summary>
    public string DeclaredContentType => _collected.Metadata.DeclaredContentType;

    /// <summary>
    /// Decoded bytes the collector observed. Exact only when
    /// <see cref="DecodedLengthIsComplete"/> is true; otherwise a lower bound.
    /// </summary>
    public long ObservedDecodedBytes => _collected.Metadata.SizeBytes;

    /// <summary>
    /// True when the collector read the whole decoded content. A failed hash can still produce a
    /// complete-looking byte count, so callers must also honour <c>ContentUnavailable</c> at
    /// collection time; such parts are not offered here.
    /// </summary>
    public bool DecodedLengthIsComplete => _collected.Metadata.SizeBytesIsComplete;

    /// <summary>The exact decoded length when known, otherwise null. Never inferred from encoded size.</summary>
    public long? KnownDecodedLength =>
        DecodedLengthIsComplete ? ObservedDecodedBytes : null;

    /// <summary>False once the analysis scope has ended; a source cannot be reopened after that.</summary>
    public bool IsValid => _scope.IsActive;

    /// <summary>
    /// Opens a fresh stream over the decoded content. Returns false for an expired source, missing
    /// content or a reopen failure. Never seeks, peeks or consumes a byte.
    /// </summary>
    public bool TryOpenContent(out Stream? stream)
    {
        stream = null;
        if (!_scope.IsActive)
        {
            return false;
        }

        var content = _collected.Part.Content;
        if (content is null)
        {
            return false;
        }

        try
        {
            stream = content.Open();
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or NotSupportedException or
            ObjectDisposedException or ArgumentException)
        {
            stream = null;
            return false;
        }
    }
}

/// <summary>One image descriptor captured during the walk; internal until the observer sees it.</summary>
internal sealed record CollectedImagePart
{
    public required int Ordinal { get; init; }

    public required string DeclaredContentType { get; init; }

    public required AttachmentMetadata Metadata { get; init; }

    public required MimePart Part { get; init; }
}

/// <summary>
/// Lifecycle token for one analysis. Every source shares it, so a single invalidation closes all
/// of them before the message is disposed.
/// </summary>
internal sealed class ImagePartScope
{
    private int _active = 1;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void Invalidate() => Interlocked.Exchange(ref _active, 0);
}
