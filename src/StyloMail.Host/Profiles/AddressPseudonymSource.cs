using System.Text;
using StyloMail.Adaptive.Profiles;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Profiles;

/// <summary>
/// Gives the profile query the tenant-keyed pseudonym function, when the deployment has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-keyed, never a plain hash.</b> An email address has a small enough space to
/// brute-force, so a plain SHA-256 over it is reversible by guessing. The same
/// <see cref="ProfileKeyHasher"/> the assessor uses is reused here, which also means addresses
/// pseudonymise identically for a profile read and for the behavioural profiles they were recorded
/// against.
/// </para>
/// <para>
/// <b>No linkage is stored.</b> The hash is computed while answering a read and discarded with the
/// request. There is no counterparty table and no join key at rest.
/// </para>
/// </remarks>
public interface IAddressPseudonymSource
{
    /// <summary>The hasher, or null when the deployment has no usable profile key.</summary>
    ProfileKeyHasher? Current { get; }
}

/// <summary>Reads the profile master key lazily, so a KPI read never forces assessor composition.</summary>
/// <remarks>
/// A missing or short key is reported as absence rather than thrown: the profile read degrades to
/// <c>no_profile_key</c>, which is the honest answer, while assessor composition remains the place
/// that refuses to start. Reading secrets lazily also keeps a deployment that never serves a
/// profile from paying for the lookup.
/// </remarks>
public sealed class AssessmentAddressPseudonymSource : IAddressPseudonymSource
{
    private readonly Lazy<ProfileKeyHasher?> _hasher;

    public AssessmentAddressPseudonymSource(IAssessmentSecretSource secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _hasher = new Lazy<ProfileKeyHasher?>(
            () =>
            {
                var key = secrets.Read().ProfileMasterKey;

                if (string.IsNullOrWhiteSpace(key)
                    || Encoding.UTF8.GetByteCount(key) < ProfileKeyHasher.MinimumKeyBytes)
                {
                    return null;
                }

                return new ProfileKeyHasher(Encoding.UTF8.GetBytes(key));
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ProfileKeyHasher? Current => _hasher.Value;
}
