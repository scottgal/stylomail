using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>Shorthand for building an <see cref="EvidenceAttribute"/>, used unqualified by this adapter.</summary>
internal static class Attr
{
    public static EvidenceAttribute Of(string name, string value) => new() { Name = name, Value = value };
}
