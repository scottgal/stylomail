using StyloMail.Core;
using StyloMail.Policy;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Policy;

/// <summary>Adapts the Profiles effective-property store to the policy read port.</summary>
public sealed class HostPolicyPropertyReader : IPolicyPropertyReader
{
    private readonly IPropertyStore _properties;

    public HostPolicyPropertyReader(IPropertyStore properties) => _properties = properties;

    public async Task<PolicyPropertyReadResult> ReadAsync(
        string tenantId,
        PolicyPropertyScope scope,
        IReadOnlyList<string> keys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var chain = Chain(scope);
            var effective = await _properties
                .GetEffectiveAsync(tenantId, chain, keys, now, cancellationToken)
                .ConfigureAwait(false);

            var facts = effective.Select(property => new PolicyPropertyFact
            {
                Key = property.Key,
                Value = property.Value,
                State = property.State.ToString(),
                Source = property.Source?.ToString(),
                Scope = property.Scope?.ToString(),
                Availability = property.Availability.State.ToString(),
                AvailabilityReason = property.Availability.Reason,
            }).ToList();

            // A returned Unknown is a healthy answer, not a read failure. The one named exception is
            // a country with only ineligible candidates: the key was answered, but no eligible
            // value exists, so a rule that requires country must escalate.
            var missing = facts
                .Where(fact => fact.Key == PolicyPropertyKeys.Country
                    && string.Equals(
                        fact.AvailabilityReason,
                        AvailabilityReasons.OnlyIneligibleCandidates,
                        StringComparison.Ordinal))
                .Select(fact => fact.Key)
                .ToArray();

            return new PolicyPropertyReadResult
            {
                Facts = facts,
                MissingKeys = missing,
                Unavailable = false,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PolicyPropertyReadResult
            {
                Facts = [],
                MissingKeys = [.. keys],
                Unavailable = true,
            };
        }
    }

    private static PropertyScopeChain Chain(PolicyPropertyScope scope)
    {
        var ancestors = new List<PropertyScope>();
        if (!string.IsNullOrWhiteSpace(scope.Company))
        {
            ancestors.Add(new PropertyScope(PropertyScopeKind.Company, scope.Company));
        }

        if (!string.IsNullOrWhiteSpace(scope.Domain))
        {
            ancestors.Add(new PropertyScope(PropertyScopeKind.Domain, scope.Domain));
        }

        if (!string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            if (!string.IsNullOrWhiteSpace(scope.EntityId))
            {
                ancestors.Insert(0, new PropertyScope(PropertyScopeKind.Entity, scope.EntityId));
            }

            return PropertyScopeChain.Create(
                new PropertyScope(PropertyScopeKind.Conversation, scope.ConversationId),
                [.. ancestors]);
        }

        var entity = new PropertyScope(PropertyScopeKind.Entity, scope.EntityId
            ?? throw new ArgumentException("A policy property read needs an entity or conversation scope."));

        return PropertyScopeChain.Create(entity, [.. ancestors]);
    }
}

/// <summary>Adapts the landed binding store to the read-only policy port.</summary>
public sealed class HostPolicyBindingReader : IPolicyBindingReader
{
    private readonly SqlitePolicyBindingStore _bindings;

    public HostPolicyBindingReader(SqlitePolicyBindingStore bindings) => _bindings = bindings;

    public async Task<IReadOnlyList<PolicyBinding>> ListForResolutionAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var records = await _bindings
            .ListAsync(tenantId, scopeKind: null, scopeId: null, activeOnly: false, now, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. records
                .Where(record => record.State == PolicyBindingState.Active
                    || (record.State == PolicyBindingState.Expired
                        && record.Binding.OnExpiry == BindingOnExpiry.HoldForReview))
                .Select(record => record.Binding),
        ];
    }
}
