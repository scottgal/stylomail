using System.Text;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The spool root is a containment boundary: a payload either lives under it or is refused.
/// </summary>
/// <remarks>
/// The interesting input is a tenant that is <em>legal</em>: <c>.</c> and <c>..</c> are made only of
/// characters the spool's sanitiser preserves, so a check that merely strips separators does not see
/// them. The writer used to build its path from that sanitiser alone while the reader had a
/// containment guard, which is the worst split: the queue could accept a payload its own reader
/// would refuse, or write outside the root entirely.
/// </remarks>
public class SpoolContainmentTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("must stay inside the spool");

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task A_tenant_that_names_a_relative_path_is_refused_by_write_and_by_read(string tenant)
    {
        using var h = new QueueHarness();

        // Write: refused, and refused before anything is created.
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Spool.WriteAsync(tenant, "q-hostile", Payload));

        Assert.Empty(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));

        // The escape this guards against leaves the payload beside the spool root, in the
        // harness's own directory. Assert on that exact artefact, not just on the return value: a
        // writer that wrote first and threw afterwards would pass the exception assertion.
        var harnessRoot = Path.GetDirectoryName(h.SpoolRoot)!;
        Assert.False(File.Exists(Path.Combine(harnessRoot, "q-hostile.eml")));

        // Read: the same segment, on every read entry point.
        var reference = $"spool://{tenant}/q-hostile.eml";
        Assert.Throws<ArgumentException>(() => h.Spool.OpenRead(reference));
        Assert.Throws<ArgumentException>(() => h.Spool.Exists(reference));
        Assert.Throws<ArgumentException>(() => h.Spool.Delete(reference));

        // And acceptance, which is where a bad tenant would actually arrive, gets no row and no
        // bytes rather than a reference the delivery worker could never open.
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Store.AcceptAsync(QueueHarness.Submission(tenantId: tenant)));
        Assert.Empty(await h.Store.CountByStateAsync());
    }

    [Fact]
    public async Task An_ordinary_tenant_still_round_trips_including_one_with_dots()
    {
        using var h = new QueueHarness();

        // The control for the refusals above: dots are legal in a name, and only the two segments
        // that mean "here" and "up" are refused. If this went red the guard would be refusing
        // everything, which would also make the refusal tests above pass for no reason.
        const string tenant = "acme.example.test";
        var reference = await h.Spool.WriteAsync(tenant, "q-normal", Payload);

        Assert.Equal("spool://acme.example.test/q-normal.eml", reference);
        Assert.True(h.Spool.Exists(reference));
        Assert.True(File.Exists(Path.Combine(h.SpoolRoot, tenant, "q-normal.eml")));

        using var stream = h.Spool.OpenRead(reference)!;
        using var reader = new StreamReader(stream);
        Assert.Equal("must stay inside the spool", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task An_accepted_message_reference_is_one_the_reader_can_open()
    {
        using var h = new QueueHarness();

        // The contract the write-side guard exists to restore: whatever acceptance stored is
        // readable by the same component, so "accepted but undeliverable" cannot be produced by a
        // tenant identifier.
        var queueId = await h.AcceptAsync(QueueHarness.Submission());
        var item = await h.Store.GetItemAsync(queueId);

        Assert.NotNull(item);
        Assert.True(h.Spool.Exists(item!.Envelope.PayloadReference));
        Assert.NotNull(h.Spool.OpenRead(item.Envelope.PayloadReference));
    }
}
