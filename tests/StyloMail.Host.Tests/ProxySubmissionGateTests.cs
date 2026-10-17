using StyloMail.AccessProxy.Submission;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The default submission gate: semantic absence relays with its evidence stated, and only a
/// deterministic finding refuses. The staged bytes are released on every path.
/// </summary>
public sealed class ProxySubmissionGateTests
{
    [Fact]
    public async Task With_no_semantic_provider_the_gate_relays_and_says_so()
    {
        var gate = new HostProxySubmissionAssessment(new AllowAllProxySubmissionPolicy());
        var stager = new InMemoryEphemeralSubmissionStager();
        await using var staged = await stager.CreateAsync(1_024, CancellationToken.None);

        var result = await gate.AssessAsync(staged, CancellationToken.None);

        // SemanticUnavailable is not a refusal. The SMTP session relays everything except a
        // deterministic Refuse, and an unavailable provider must never block outbound mail.
        Assert.Equal(ProxySubmissionDisposition.SemanticUnavailable, result.Disposition);
        Assert.NotEqual(ProxySubmissionDisposition.Refuse, result.Disposition);
        Assert.False(result.IsDeterministic);
        Assert.StartsWith("sub_", result.SubmissionId);
    }

    [Fact]
    public async Task A_deterministic_finding_refuses()
    {
        var gate = new HostProxySubmissionAssessment(new RefusingPolicy());
        var stager = new InMemoryEphemeralSubmissionStager();
        await using var staged = await stager.CreateAsync(1_024, CancellationToken.None);

        var result = await gate.AssessAsync(staged, CancellationToken.None);

        Assert.Equal(ProxySubmissionDisposition.Refuse, result.Disposition);
        Assert.True(result.IsDeterministic);
    }

    [Fact]
    public async Task A_staged_handle_releases_its_bytes_on_dispose()
    {
        var stager = new InMemoryEphemeralSubmissionStager();
        var staged = await stager.CreateAsync(1_024, CancellationToken.None);
        await staged.Content.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(1, stager.ActiveCount);

        await staged.DisposeAsync();

        // Every terminal path in the SMTP session disposes the handle; this is the property that
        // makes "staged bytes are gone after a refusal" true rather than hoped for.
        Assert.Equal(0, stager.ActiveCount);
        Assert.Throws<ObjectDisposedException>(() => staged.Content.Length);
    }

    private sealed class RefusingPolicy : IProxySubmissionPolicy
    {
        public ValueTask<ProxySubmissionPolicyResult> EvaluateAsync(
            IStagedProxySubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new ProxySubmissionPolicyResult
            {
                Refuse = true,
                Reason = "deterministic test finding",
            });
    }
}
