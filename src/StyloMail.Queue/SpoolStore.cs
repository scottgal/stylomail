using System.Runtime.InteropServices;

namespace StyloMail.Queue;

/// <summary>Raised when the spool cannot durably store a payload.</summary>
/// <remarks>
/// Callers must translate this into a temporary SMTP failure. It must never be swallowed into a
/// successful acceptance: disk full is exactly the condition under which accepting mail would
/// destroy it.
/// </remarks>
public sealed class SpoolUnavailableException : Exception
{
    public SpoolUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Durable, per-tenant payload spool on the local filesystem.
/// </summary>
/// <remarks>
/// Writes are atomic: content goes to a temporary file in the destination directory, is flushed
/// to stable storage, and is then renamed into place. A reader therefore never observes a partial
/// payload, and a crash leaves either the complete file or only a sweepable temporary.
///
/// <para>
/// <b>Payload-before-metadata ordering is deliberate.</b> The payload is written and flushed first,
/// and only then does the queue commit the metadata row that references it. That ordering means a
/// crash mid-acceptance can leave an <em>orphan payload</em>, which a sweeper can safely delete, but
/// can never leave <em>metadata pointing at a payload that does not exist</em>, which would be
/// unrecoverable mail loss.
/// </para>
/// </remarks>
public sealed class SpoolStore
{
    /// <summary>
    /// Suffix of a payload being written, before it is renamed into place.
    /// </summary>
    /// <remarks>
    /// Shared between the writer and the sweeper on purpose. These were two independent string
    /// literals: the writer produced <c>name.{guid}.tmp</c> and the sweeper searched for
    /// <c>*.tmp</c>, so they agreed, but only by coincidence, and a change to either would have
    /// left crashed temporaries accumulating on disk forever with nothing to notice.
    /// </remarks>
    private const string TemporarySuffix = ".tmp";

    private readonly string _root;

    public SpoolStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>
    /// Durably writes a payload and returns its reference. Throws
    /// <see cref="SpoolUnavailableException"/> if it cannot be made durable.
    /// </summary>
    /// <remarks>
    /// The final file is never overwritten. Queue ids are generated fresh per acceptance and are
    /// never reused, so a file already present at the destination is by definition a leftover from
    /// an acceptance that crashed after the write and before the metadata commit. Refusing to
    /// clobber it keeps "a spool file is only ever written once" true, which is what makes the
    /// orphan sweep safe to reason about.
    /// </remarks>
    public async Task<string> WriteAsync(
        string tenantId,
        string queueId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        ArgumentNullException.ThrowIfNull(tenantId);

        // Tenant is a directory level so a per-tenant sweep and per-tenant byte accounting are
        // both straightforward.
        var directory = Path.Combine(_root, Sanitize(tenantId));
        var finalPath = Path.Combine(directory, $"{Sanitize(queueId)}.eml");

        // A unique temporary name: two writers racing on the same queue id would otherwise share
        // one temporary path and interleave their bytes into it.
        var tempPath = Path.Combine(directory, $"{Sanitize(queueId)}.{Guid.NewGuid():N}{TemporarySuffix}");

        try
        {
            Directory.CreateDirectory(directory);

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

                // Flush to stable storage before the rename. Without this, the rename can be
                // visible to a reader while the content is still only in the page cache.
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: false);

            // The rename itself is a directory-entry change, and a flushed file does not make its
            // directory entry durable. Without this, a power loss can leave the metadata row,             // committed to SQLite with a full fsync of its own, referring to a payload whose
            // name never made it to disk. That is the one state this component must never reach.
            FlushDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);

            // Disk full, permissions, unmounted volume, all of these mean "do not accept".
            throw new SpoolUnavailableException(
                $"Payload could not be durably spooled for queue item {queueId}.", ex);
        }

        return ReferenceFor(tenantId, queueId);
    }

    /// <summary>Opens a spooled payload for reading, or null if it is gone.</summary>
    public Stream? OpenRead(string payloadReference)
    {
        var path = PathFor(payloadReference);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    /// <summary>Whether the payload named by this reference is still present.</summary>
    public bool Exists(string payloadReference) => File.Exists(PathFor(payloadReference));

    /// <summary>Deletes a payload. Absence is not an error, deletion must be idempotent.</summary>
    public void Delete(string payloadReference) => TryDelete(PathFor(payloadReference));

    /// <summary>
    /// Finds payloads and abandoned temporaries with no live queue metadata, without deleting them.
    /// </summary>
    /// <param name="liveReferences">References the queue's metadata currently names.</param>
    /// <param name="cutoff">
    /// Only files last written strictly before this instant are candidates.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The cutoff is not optional, and is not merely housekeeping.</b> Acceptance writes the
    /// payload and only commits the metadata that references it afterwards, so between those two
    /// steps an in-flight acceptance is byte-for-byte indistinguishable from a true orphan. A sweep
    /// that acted on the difference alone would delete a payload microseconds before its metadata
    /// committed, manufacturing precisely the unrecoverable state this subsystem exists to prevent.
    /// The window must comfortably exceed the acceptance critical section.
    /// </para>
    /// <para>
    /// The cutoff is supplied by the caller rather than read here so that spool time comes from the
    /// same injected clock as the rest of the queue. File timestamps are always UTC.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> FindOrphans(IReadOnlySet<string> liveReferences, DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(liveReferences);

        if (!Directory.Exists(_root))
        {
            return [];
        }

        var orphans = new List<string>();
        foreach (var file in EnumerateCandidates())
        {
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');

            if (relative.EndsWith(".eml", StringComparison.Ordinal)
                && liveReferences.Contains($"spool://{relative}"))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime)
            {
                continue;
            }

            orphans.Add($"spool://{relative}");
        }

        return orphans;
    }

    /// <summary>
    /// Deletes orphan payloads and abandoned temporaries, returning what was removed.
    /// </summary>
    /// <remarks>
    /// These are safe to delete precisely because payload-before-metadata ordering guarantees the
    /// only possible orphan is a payload nobody references, never metadata referencing nothing.
    /// </remarks>
    public IReadOnlyList<string> SweepOrphans(IReadOnlySet<string> liveReferences, DateTimeOffset cutoff)
    {
        var orphans = FindOrphans(liveReferences, cutoff);
        var swept = new List<string>(orphans.Count);

        foreach (var reference in orphans)
        {
            StripReference(reference, out var relative);
            var path = ResolveWithinRoot(relative);
            if (path is null)
            {
                continue;
            }

            TryDelete(path);
            if (!File.Exists(path))
            {
                swept.Add(reference);
            }
        }

        return swept;
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.eml", SearchOption.AllDirectories))
        {
            yield return file;
        }

        // A crash between creating a temporary and renaming it leaves a file nothing will ever
        // reference. It is not an orphan payload, but it is the same kind of debris, a distinct
        // path from an unreferenced payload, and one that leaks disk on every crashed acceptance
        // if it goes unswept.
        foreach (var file in Directory.EnumerateFiles(
            _root, $"*{TemporarySuffix}", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private static string ReferenceFor(string tenantId, string queueId)
        => $"spool://{Sanitize(tenantId)}/{Sanitize(queueId)}.eml";

    private string PathFor(string payloadReference)
    {
        StripReference(payloadReference, out var relative);
        return ResolveWithinRoot(relative)
            ?? throw new ArgumentException(
                "Spool reference escapes the spool root.", nameof(payloadReference));
    }

    private static void StripReference(string payloadReference, out string relative)
    {
        const string prefix = "spool://";
        if (!payloadReference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a spool reference: {payloadReference}", nameof(payloadReference));
        }

        relative = payloadReference[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Resolves a relative path inside the spool, or null if it escapes the root.</summary>
    private string? ResolveWithinRoot(string relative)
    {
        try
        {
            // Defence in depth: a reference must not be able to escape the spool root.
            var full = Path.GetFullPath(Path.Combine(_root, relative));
            var rootFull = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort. A leftover temporary is swept later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Flushes a directory entry to stable storage, making the preceding rename durable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unix-specific by nature. Windows has no portable equivalent and the call is skipped there;
    /// on Unix, <c>fsync</c> on a directory file descriptor is the documented way to commit a
    /// directory-entry change.
    /// </para>
    /// <para>
    /// A failure is <b>fail-closed</b>: a rename we cannot prove durable is not a payload we can
    /// honestly claim to have stored, and the caller must defer rather than accept. The one
    /// exception is <c>EINVAL</c>/<c>ENOTSUP</c>, which some filesystems return to mean "this is
    /// not supported here" rather than "this failed", those are tolerated, with the residual risk
    /// documented rather than hidden.
    /// </para>
    /// <para>
    /// <b>Known limitation:</b> on macOS/APFS, <c>fsync</c> on a directory is not a full
    /// durability barrier (<c>F_FULLFSYNC</c> is required for that and applies to file contents).
    /// On Linux, where this deployment realistically runs, directory <c>fsync</c> is the real
    /// mechanism and this closes the window.
    /// </para>
    /// </remarks>
    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fd = Posix.Open(directory, Posix.O_RDONLY);
        if (fd < 0)
        {
            throw new SpoolUnavailableException(
                $"Could not open spool directory '{directory}' to flush it (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            if (Posix.Fsync(fd) == 0)
            {
                return;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Posix.EINVAL || errno == Posix.ENOTSUP)
            {
                // The filesystem does not support directory fsync. Continue, carrying the
                // residual risk, rather than refusing all mail on an otherwise working volume.
                return;
            }

            throw new SpoolUnavailableException(
                $"Could not flush spool directory '{directory}' to stable storage (errno {errno}).");
        }
        finally
        {
            Posix.Close(fd);
        }
    }

    /// <summary>
    /// The three libc calls needed to make a rename durable.
    /// </summary>
    /// <remarks>
    /// <c>DllImport</c> rather than <c>LibraryImport</c>: the source-generated version requires
    /// <c>AllowUnsafeBlocks</c> on the whole assembly, and turning on unsafe code across a security
    /// component to reach one syscall is the wrong trade. <c>SYSLIB1054</c>, which suggests the
    /// migration, is suppressed here for that reason.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability",
        "SYSLIB1054",
        Justification = "LibraryImport requires AllowUnsafeBlocks for the whole assembly.")]
    private static class Posix
    {
        internal const int O_RDONLY = 0;
        internal const int EINVAL = 22;

        // EOPNOTSUPP: 45 on Darwin, 95 on Linux.
        internal static readonly int ENOTSUP = OperatingSystem.IsMacOS() ? 45 : 95;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int fileDescriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int fileDescriptor);
    }
}
