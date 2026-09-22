using System.Security.Cryptography;
using System.Text;
using MimeKit;
using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>One attachment found in the message, with the parts of it we are allowed to look at.</summary>
internal sealed record CollectedAttachment
{
    public required AttachmentMetadata Metadata { get; init; }

    public required AttachmentAssessment Assessment { get; init; }

    public required long HashedBytes { get; init; }

    public required bool HashPartial { get; init; }

    public required bool IsEmbeddedMessage { get; init; }
}

/// <summary>Everything a bounded walk of the MIME tree produced.</summary>
internal sealed record CollectedContent
{
    public required IReadOnlyList<string> PlainBodies { get; init; }

    public required IReadOnlyList<string> HtmlBodies { get; init; }

    public required IReadOnlyList<CollectedAttachment> Attachments { get; init; }

    public required int PartCount { get; init; }

    public required int MaxDepth { get; init; }

    public required int SignaturePartCount { get; init; }

    public required bool HasEncryptedContainer { get; init; }

    public required bool UnparseableContent { get; init; }

    public required bool BodyTruncated { get; init; }

    public required bool BodyParsed { get; init; }

    /// <summary>Set when the walk stopped early because a structural limit was reached.</summary>
    public string? LimitBreach { get; init; }
}

/// <summary>
/// Walks the parsed MIME tree, bounded at every step.
/// </summary>
/// <remarks>
/// The walk is iterative rather than recursive so that a deeply nested message cannot exhaust the
/// stack — a nesting bomb should hit a configured limit, not a thread. It also refuses to descend
/// into attached <c>message/rfc822</c> payloads: a forwarded message is recorded as an attachment
/// with its own digest, and its contents are not recursively analysed. That keeps the work per
/// message proportional to the message rather than to the number of messages someone chose to nest
/// inside it.
/// </remarks>
internal static class PartCollector
{
    private static readonly HashSet<string> SignatureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pgp-signature",
        "application/pkcs7-signature",
        "application/x-pkcs7-signature",
    };

    private static readonly HashSet<string> EncryptedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multipart/encrypted",
        "application/pgp-encrypted",
        "application/pkcs7-mime",
        "application/x-pkcs7-mime",
        "application/x-pkcs7-encrypted",
    };

    public static CollectedContent Collect(MimeMessage message, MimeParseLimits limits)
    {
        var plain = new List<string>();
        var html = new List<string>();
        var attachments = new List<CollectedAttachment>();

        var partCount = 0;
        var maxDepth = 0;
        var signatureParts = 0;
        var encrypted = false;
        var unparseable = false;
        var bodyTruncated = false;
        var bodyParsed = false;
        var hashedTotal = 0L;
        string? breach = null;

        if (message.Body is null)
        {
            return new CollectedContent
            {
                PlainBodies = [],
                HtmlBodies = [],
                Attachments = [],
                PartCount = 0,
                MaxDepth = 0,
                SignaturePartCount = 0,
                HasEncryptedContainer = false,
                UnparseableContent = false,
                BodyTruncated = false,
                BodyParsed = false,
                LimitBreach = null,
            };
        }

        var stack = new Stack<(MimeEntity Entity, int Depth)>();
        stack.Push((message.Body, 1));

        while (stack.Count > 0)
        {
            var (entity, depth) = stack.Pop();

            if (depth > limits.MaxMimeDepth)
            {
                breach = "mime-depth";
                break;
            }

            partCount++;
            if (partCount > limits.MaxParts)
            {
                breach = "part-count";
                break;
            }

            if (depth > maxDepth)
            {
                maxDepth = depth;
            }

            switch (entity)
            {
                case Multipart multipart:
                    if (EncryptedTypes.Contains(multipart.ContentType.MimeType.ToLowerInvariant()))
                    {
                        encrypted = true;
                    }

                    // Reversed so children come off the stack in document order.
                    for (var i = multipart.Count - 1; i >= 0; i--)
                    {
                        stack.Push((multipart[i], depth + 1));
                    }

                    break;

                case MessagePart embedded:
                    // An attached message is recorded, hashed and not descended into.
                    attachments.Add(DescribeEmbedded(embedded, limits, ref hashedTotal));
                    break;

                case MimePart part:
                    var classified = Classify(part, limits, ref hashedTotal);
                    switch (classified.Kind)
                    {
                        case PartKind.PlainBody:
                            bodyParsed = true;
                            bodyTruncated |= classified.Truncated;
                            if (plain.Count < 8)
                            {
                                plain.Add(classified.Text!);
                            }
                            else
                            {
                                bodyTruncated = true;
                            }

                            break;

                        case PartKind.HtmlBody:
                            bodyParsed = true;
                            bodyTruncated |= classified.Truncated;
                            if (html.Count < 8)
                            {
                                html.Add(classified.Text!);
                            }
                            else
                            {
                                bodyTruncated = true;
                            }

                            break;

                        case PartKind.Signature:
                            signatureParts++;
                            break;

                        case PartKind.Attachment:
                            unparseable |= classified.ContentUnreadable;
                            encrypted |= classified.Assessment!.EncryptedContainer;
                            if (attachments.Count < limits.MaxAttachments)
                            {
                                attachments.Add(new CollectedAttachment
                                {
                                    Metadata = classified.Metadata!,
                                    Assessment = classified.Assessment,
                                    HashedBytes = classified.HashedBytes,
                                    HashPartial = classified.HashPartial,
                                    IsEmbeddedMessage = false,
                                });
                            }

                            break;

                        default:
                            unparseable |= classified.ContentUnreadable;
                            break;
                    }

                    break;

                default:
                    unparseable = true;
                    break;
            }
        }

        return new CollectedContent
        {
            PlainBodies = plain,
            HtmlBodies = html,
            Attachments = attachments,
            PartCount = partCount,
            MaxDepth = maxDepth,
            SignaturePartCount = signatureParts,
            HasEncryptedContainer = encrypted,
            UnparseableContent = unparseable,
            BodyTruncated = bodyTruncated,
            BodyParsed = bodyParsed,
            LimitBreach = breach,
        };
    }

    private enum PartKind
    {
        PlainBody,
        HtmlBody,
        Signature,
        Attachment,
        Other,
    }

    private sealed record Classified(
        PartKind Kind,
        string? Text = null,
        AttachmentMetadata? Metadata = null,
        AttachmentAssessment? Assessment = null,
        bool Truncated = false,
        bool ContentUnreadable = false,
        long HashedBytes = 0,
        bool HashPartial = false);

    /// <summary>
    /// Records an attached message. Its bytes are hashed through a bounded sink and it is not
    /// descended into — see the type remarks on why.
    /// </summary>
    private static CollectedAttachment DescribeEmbedded(MessagePart part, MimeParseLimits limits, ref long hashedTotal)
    {
        var fileName = FirstNonEmpty(part.ContentDisposition?.FileName, part.ContentType.Name) ?? "attached-message.eml";
        var size = 0L;
        string? hash = null;
        var partial = false;

        try
        {
            var embedded = part.Message ?? throw new InvalidOperationException("embedded message could not be read");
            var budget = BudgetFor(limits, hashedTotal);
            using var sink = new BoundedWriteStream(budget);
            embedded.WriteTo(sink, CancellationToken.None);
            partial = sink.Truncated;
            size = sink.Written;
            hashedTotal += sink.Written;
            hash = (partial ? "sha256-partial:" : "sha256:") + Convert.ToHexStringLower(sink.Digest());
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or NotSupportedException)
        {
            // A part we cannot read is recorded as unavailable rather than skipped silently.
        }

        var assessment = AttachmentTypes.Assess(fileName, "message/rfc822");
        var metadata = new AttachmentMetadata
        {
            FileName = fileName,
            DeclaredContentType = "message/rfc822",
            ExtensionImpliedContentType = "message/rfc822",
            SizeBytes = size,
            SizeBytesIsComplete = !partial,
            ContentHash = hash,
            ContentUnavailable = hash is null,
        };

        return new CollectedAttachment
        {
            Metadata = metadata,
            Assessment = assessment,
            HashedBytes = size,
            HashPartial = partial,
            IsEmbeddedMessage = true,
        };
    }

    private static Classified Classify(MimePart part, MimeParseLimits limits, ref long hashedTotal)
    {
        var contentType = part.ContentType.MimeType.ToLowerInvariant();
        var disposition = part.ContentDisposition?.Disposition?.ToLowerInvariant();
        var fileName = FirstNonEmpty(part.ContentDisposition?.FileName, part.FileName, part.ContentType.Name);
        var hasFileName = !string.IsNullOrEmpty(fileName);

        if (SignatureTypes.Contains(contentType))
        {
            return new Classified(PartKind.Signature);
        }

        var isTextual = contentType is "text/plain" or "text/html";
        var isBodyPart = isTextual && !string.Equals(disposition, "attachment", StringComparison.Ordinal) && !hasFileName;

        if (isBodyPart && part is TextPart textPart)
        {
            try
            {
                var text = textPart.Text ?? string.Empty;
                var truncated = false;
                if (text.Length > limits.MaxBodyChars)
                {
                    text = text[..limits.MaxBodyChars];
                    truncated = true;
                }

                return new Classified(
                    contentType == "text/html" ? PartKind.HtmlBody : PartKind.PlainBody,
                    Text: text,
                    Truncated: truncated);
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException)
            {
                return new Classified(PartKind.Other, ContentUnreadable: true);
            }
        }

        // Everything else is an attachment: inline images, named text parts, calendars, executables.
        var name = fileName ?? "unnamed-part";
        var encryptedContainer = AttachmentTypes.IsEncryptedContainer(name, contentType) ||
                                 EncryptedTypes.Contains(contentType);

        var size = 0L;
        string? hash = null;
        var partial = false;
        var unavailable = false;

        try
        {
            var content = part.Content ?? throw new InvalidOperationException("part content could not be read");
            using var stream = content.Open();
            (hash, size, partial) = HashStream(stream, limits, ref hashedTotal);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or NotSupportedException)
        {
            unavailable = true;
        }

        var assessment = AttachmentTypes.Assess(name, contentType);
        var extensionImplied = AttachmentTypes.ImpliedContentType(name);

        var metadata = new AttachmentMetadata
        {
            FileName = name,
            DeclaredContentType = contentType,
            ExtensionImpliedContentType = extensionImplied,
            SizeBytes = size,
            SizeBytesIsComplete = !partial,
            ContentHash = hash,
            ContentUnavailable = unavailable || encryptedContainer,
        };

        return new Classified(
            PartKind.Attachment,
            Metadata: metadata,
            Assessment: assessment with { EncryptedContainer = encryptedContainer },
            ContentUnreadable: unavailable,
            HashedBytes: size,
            HashPartial: partial);
    }

    /// <summary>How many more bytes may be hashed before the total budget for this message runs out.</summary>
    private static long BudgetFor(MimeParseLimits limits, long hashedTotal) =>
        Math.Min(limits.MaxAttachmentHashBytes, Math.Max(0, limits.MaxTotalAttachmentHashBytes - hashedTotal));

    /// <summary>
    /// Reads a part's decoded content up to the hashing budget and returns its SHA-256.
    /// </summary>
    /// <remarks>
    /// When a part is larger than the budget the digest is taken over the prefix and flagged as
    /// partial rather than reported as if it covered the whole file — two different files that
    /// share a prefix would otherwise collide under a hash that claimed to be complete.
    /// </remarks>
    private static (string? Hash, long Bytes, bool Partial) HashStream(Stream stream, MimeParseLimits limits, ref long hashedTotal)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        var written = 0L;
        var partial = false;

        var perPartBudget = BudgetFor(limits, hashedTotal);
        if (perPartBudget <= 0)
        {
            return (null, 0, true);
        }

        while (written < perPartBudget)
        {
            var want = (int)Math.Min(buffer.Length, perPartBudget - written);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            written += read;
        }

        if (stream.ReadByte() >= 0)
        {
            partial = true;
        }

        sha.TransformFinalBlock([], 0, 0);
        hashedTotal += written;

        var prefix = partial ? "sha256-partial:" : "sha256:";
        return (prefix + Convert.ToHexStringLower(sha.Hash!), written, partial);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
