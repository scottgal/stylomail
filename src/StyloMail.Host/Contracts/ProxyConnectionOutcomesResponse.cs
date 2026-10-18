using StyloMail.Host.Proxy;

namespace StyloMail.Host.Contracts;

public sealed record ProxyConnectionOutcomesResponse(string TenantId, DateTimeOffset From,
    DateTimeOffset Through, IReadOnlyList<ProxyConnectionBucket> Buckets);
