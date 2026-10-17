using System.Text;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.AccessProxy.Threading;

public interface IThreadTokenDeriver
{
    string Derive(string tenantId, string value);
}

/// <summary>Bounded allowlist parser for message threading headers; it stops at the first blank line.</summary>
public sealed class ThreadingHeaderExtractor
{
    private readonly IThreadTokenDeriver _tokens;
    private readonly int _maximumHeaderBytes;

    public ThreadingHeaderExtractor(IThreadTokenDeriver tokens, int maximumHeaderBytes = 16 * 1024)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        if (maximumHeaderBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        _maximumHeaderBytes = maximumHeaderBytes;
    }

    public ProxyThreadingEvent Extract(string tenantId, string accountId, ReadOnlySpan<byte> headerBlock, DateTimeOffset timestamp)
    {
        if (headerBlock.Length > _maximumHeaderBytes) throw new InvalidOperationException("Header block exceeded bound.");
        var text = Encoding.UTF8.GetString(headerBlock);
        string? messageId = null; var references = new List<string>(); string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) break;
            if ((line[0] is ' ' or '\t') && current is not null)
            {
                if (current == "references") references.Add(line.Trim());
                continue;
            }
            var colon = line.IndexOf(':'); if (colon < 0) { current = null; continue; }
            current = line[..colon].Trim().ToLowerInvariant(); var value = line[(colon + 1)..].Trim();
            if (current == "message-id") messageId = value;
            else if (current == "in-reply-to" || current == "references") references.Add(value);
        }
        var identity = messageId ?? "self";
        var threadSeed = references.Count == 0 ? identity : references[0];
        return new ProxyThreadingEvent { TenantId = tenantId, AccountId = accountId, Protocol = ProxyListenerProtocol.Imap,
            MessageToken = _tokens.Derive(tenantId, identity), ThreadToken = _tokens.Derive(tenantId, threadSeed),
            ReferenceDepth = Math.Max(1, references.Count + 1), Timestamp = timestamp };
    }
}
