namespace StyloMail.Mime;

/// <summary>What the file name and declared type say about one attachment, kept separate from its bytes.</summary>
internal sealed record AttachmentAssessment
{
    public required string FileName { get; init; }

    public required string DeclaredContentType { get; init; }

    public string? ImpliedContentType { get; init; }

    /// <summary>The name implies one type and the part declares another, both specific.</summary>
    public required bool TypeMismatch { get; init; }

    /// <summary>The declared type carries no information (<c>application/octet-stream</c>).</summary>
    public required bool GenericDeclaredType { get; init; }

    /// <summary>Two extensions, the last one executable or script-like.</summary>
    public required bool DoubleExtension { get; init; }

    /// <summary>A right-to-left override in the name, used to disguise the real extension.</summary>
    public required bool DirectionOverrideInName { get; init; }

    public required bool ExecutableExtension { get; init; }

    public required bool EncryptedContainer { get; init; }
}

/// <summary>
/// File-name to content-type expectations for attachments.
/// </summary>
/// <remarks>
/// The map is deliberately a small allow-list of extensions whose type is well known. An extension
/// that is not in it produces no conclusion at all, "unknown" is a distinct state here as much as
/// it is anywhere else in this system, and guessing would manufacture mismatches out of unfamiliar
/// file types rather than out of deception.
/// </remarks>
internal static class AttachmentTypes
{
    private static readonly Dictionary<string, string> Implied = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "text/xml",
        [".json"] = "application/json",
        [".rtf"] = "application/rtf",
        [".ics"] = "text/calendar",
        [".eml"] = "message/rfc822",
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".exe"] = "application/x-msdownload",
        [".dll"] = "application/x-msdownload",
        [".msi"] = "application/x-msi",
        [".scr"] = "application/x-msdownload",
        [".bat"] = "application/x-msdownload",
        [".cmd"] = "application/x-msdownload",
        [".ps1"] = "text/plain",
        [".vbs"] = "text/plain",
        [".js"] = "text/javascript",
        [".jar"] = "application/java-archive",
        [".iso"] = "application/x-iso9660-image",
        [".img"] = "application/octet-stream",
        [".lnk"] = "application/x-ms-shortcut",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".heic"] = "image/heic",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
    };

    /// <summary>Extensions that are executed or run rather than opened.</summary>
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".bat", ".cmd", ".com", ".pif", ".msi", ".msp",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1", ".hta",
        ".jar", ".lnk", ".url", ".reg", ".iso", ".img", ".apk", ".dmg", ".app",
    };

    /// <summary>Containers that are encrypted or password-protected as a matter of course.</summary>
    private static readonly HashSet<string> EncryptedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pgp", ".gpg", ".p7m", ".p7z", ".age", ".enc",
    };

    /// <summary>Content types that mean the payload is not readable without a key.</summary>
    private static readonly HashSet<string> EncryptedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pgp-encrypted",
        "application/pkcs7-mime",
        "application/x-pkcs7-mime",
        "multipart/encrypted",
        "application/x-pkcs7-encrypted",
    };

    public static string? ImpliedContentType(string? fileName)
    {
        var extension = ExtensionOf(fileName);
        return extension is not null && Implied.TryGetValue(extension, out var type) ? type : null;
    }

    public static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var name = fileName.Trim().TrimEnd('.');
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? null : name[dot..];
    }

    /// <summary>All extensions in a file name, outermost last, used to spot <c>invoice.pdf.exe</c>.</summary>
    public static IReadOnlyList<string> ExtensionsOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return [];
        }

        var name = fileName.Trim();
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? [] : [.. parts.Skip(1).Select(p => "." + p)];
    }

    public static bool IsExecutableExtension(string? extension) =>
        extension is not null && Executable.Contains(extension);

    public static bool IsEncryptedContainer(string? fileName, string declaredContentType)
    {
        if (EncryptedContentTypes.Contains(declaredContentType))
        {
            return true;
        }

        var extension = ExtensionOf(fileName);
        return extension is not null && EncryptedExtensions.Contains(extension);
    }

    public static bool IsGenericDeclaredType(string declaredContentType) =>
        declaredContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
        declaredContentType.Equals("application/binary", StringComparison.OrdinalIgnoreCase) ||
        declaredContentType.Equals("application/unknown", StringComparison.OrdinalIgnoreCase) ||
        declaredContentType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="text"/> contains a right-to-left override or embedding.</summary>
    public static bool HasDirectionOverride(string? text) =>
        !string.IsNullOrEmpty(text) &&
        text.Any(c => c is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u2066' or '\u2067');

    /// <summary>Assesses one attachment's name and declared type. Never touches the content.</summary>
    public static AttachmentAssessment Assess(string fileName, string declaredContentType)
    {
        var declared = declaredContentType.ToLowerInvariant();
        var implied = ImpliedContentType(fileName);

        var extensions = ExtensionsOf(fileName);
        var lastExtension = extensions.Count > 0 ? extensions[^1] : null;
        var doubleExtension = extensions.Count > 1 && IsExecutableExtension(lastExtension);

        return new AttachmentAssessment
        {
            FileName = fileName,
            DeclaredContentType = declared,
            ImpliedContentType = implied,
            TypeMismatch = implied is not null &&
                           !IsGenericDeclaredType(declared) &&
                           !TypesAgree(implied, declared),
            GenericDeclaredType = IsGenericDeclaredType(declared),
            DoubleExtension = doubleExtension,
            DirectionOverrideInName = HasDirectionOverride(fileName),
            ExecutableExtension = IsExecutableExtension(lastExtension),
            EncryptedContainer = IsEncryptedContainer(fileName, declared),
        };
    }

    /// <summary>
    /// Whether a declared type is consistent with the type a file name implies.
    /// </summary>
    /// <remarks>
    /// Browsers and mail clients are loose about this, so the comparison is loose too: a shared
    /// family (<c>text/*</c>, <c>image/*</c>, the two OOXML/legacy office types) counts as
    /// agreement, and only a genuinely different family is a mismatch. A check that fired on
    /// every <c>text/xml</c> versus <c>application/xml</c> would be noise, and noise is what
    /// makes people turn these signals off.
    /// </remarks>
    private static bool TypesAgree(string implied, string declared)
    {
        if (implied.Equals(declared, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Parameter-stripped comparison.
        var impliedBase = implied.Split(';')[0].Trim();
        var declaredBase = declared.Split(';')[0].Trim();
        if (impliedBase.Equals(declaredBase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var impliedFamily = Family(impliedBase);
        var declaredFamily = Family(declaredBase);
        if (impliedFamily is not null && impliedFamily.Equals(declaredFamily, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Office documents: the legacy and OOXML variants are frequently cross-declared.
        return IsOffice(impliedBase) && IsOffice(declaredBase);
    }

    private static string? Family(string contentType)
    {
        var slash = contentType.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var family = contentType[..slash];
        return family is "text" or "image" or "audio" or "video" ? family : null;
    }

    private static bool IsOffice(string contentType) =>
        contentType.Contains("officedocument", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("ms-", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/msword", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/rtf", StringComparison.OrdinalIgnoreCase);
}
