using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// The exact key a semantic assessment is memoised under.
/// </summary>
/// <remarks>
/// <b>Exact, and deliberately so.</b> There is no fuzzy key, no near-match path and no
/// "sufficiently similar" short circuit. A near-match is not the same question, and answering a
/// question with a lookalike's answer is how a reused template with a swapped bank account
/// inherits a previously-clean result. Near-duplicate detection exists in this system, but it
/// produces <em>campaign evidence</em> and is never consulted here.
///
/// <para>
/// The key is the composition of five things, and all five are load-bearing:
/// </para>
/// <list type="number">
/// <item><b>Tenant</b>, the same bytes in two tenants are two different questions, and a shared
/// entry would move one tenant's evidence into another's ledger.</item>
/// <item><b>Resolved model version</b>, an alias moves without notice, and a verdict from a
/// different model is not a cached version of this one.</item>
/// <item><b>Question schema version</b>, adding or reworded dimensions make old answers
/// incomparable, not merely old.</item>
/// <item><b>Preprocessing version</b>, the same model asked about differently-prepared content is
/// a different question.</item>
/// <item><b>A digest of the complete canonical classifier input</b>, the message as the classifier
/// sees it, including tagged context and relationship context when either went in.</item>
/// </list>
/// </remarks>
public static class SemanticCacheKey
{
    /// <summary>Stamp for the key construction itself.</summary>
    public const string KeySchemeVersion = "semantic-cache-key/1";

    /// <summary>
    /// The key digest for one input under one configuration.
    /// </summary>
    /// <remarks>
    /// The configured model version is mixed in even though the provider reports a resolved one,
    /// because a key must be computable before the call. The resolved version is recorded on the
    /// entry and re-checked on every lookup, so a provider that starts resolving a different model
    /// invalidates the corpus rather than being papered over.
    /// </remarks>
    public static string Digest(SemanticMailInput input, SemanticCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder()
            .Append(KeySchemeVersion).Append('\n')
            .Append(LengthPrefixed(options.ClassifierModelVersion)).Append('\n')
            .Append(LengthPrefixed(options.QuestionSchemaVersion)).Append('\n')
            .Append(LengthPrefixed(options.PreprocessingVersion)).Append('\n')
            .Append(LengthPrefixed(ClassifierInputCanonicalizer.Canonicalize(input)));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string LengthPrefixed(string value) => $"{value.Length}:{value}";
}
