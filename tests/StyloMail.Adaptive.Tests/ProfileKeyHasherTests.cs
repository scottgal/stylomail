using StyloMail.Adaptive.Profiles;

namespace StyloMail.Adaptive.Tests;

public class ProfileKeyHasherTests
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    [Fact]
    public void SameIdentityHashesIdenticallyWithinATenant()
    {
        var hasher = new ProfileKeyHasher(MasterKey);

        Assert.Equal(
            hasher.Hash("tenant-a", "alice@example.com"),
            hasher.Hash("tenant-a", "alice@example.com"));
    }

    [Fact]
    public void SameIdentityHashesDifferentlyAcrossTenants()
    {
        var hasher = new ProfileKeyHasher(MasterKey);

        // Tenant-scoped keys mean a stolen profile row from one tenant tells an attacker
        // nothing about whether the same address exists in another.
        Assert.NotEqual(
            hasher.Hash("tenant-a", "alice@example.com"),
            hasher.Hash("tenant-b", "alice@example.com"));
    }

    [Fact]
    public void CaseAndSurroundingWhitespaceDoNotSplitAProfile()
    {
        var hasher = new ProfileKeyHasher(MasterKey);

        Assert.Equal(
            hasher.Hash("tenant-a", "Alice@Example.com"),
            hasher.Hash("tenant-a", "  alice@example.com  "));
    }

    [Fact]
    public void KeyLeaksNoPartOfTheRawAddress()
    {
        var hasher = new ProfileKeyHasher(MasterKey);

        var key = hasher.Hash("tenant-a", "alice@example.com");

        Assert.DoesNotContain("alice", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example", key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, key.Length);
    }

    [Fact]
    public void ADifferentMasterKeyProducesADifferentPseudonym()
    {
        var first = new ProfileKeyHasher(MasterKey);
        var rotated = new ProfileKeyHasher([.. MasterKey.Reverse()]);

        // Rotation invalidates the mapping rather than silently aliasing two identities.
        Assert.NotEqual(
            first.Hash("tenant-a", "alice@example.com"),
            rotated.Hash("tenant-a", "alice@example.com"));
    }

    [Fact]
    public void HasherRejectsAWeakMasterKey()
    {
        Assert.Throws<ArgumentException>(() => new ProfileKeyHasher(new byte[8]));
    }
}
