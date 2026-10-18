using System.Diagnostics;
using System.Net;
using StyloMail.Core;

namespace StyloMail.Nimble.Tests;

/// <summary>
/// The deadline must cover the response body, not only the headers.
/// </summary>
/// <remarks>
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> reports completion when the headers arrive.
/// The body read was previously issued with the caller's token instead of the adapter's deadline
/// token, so a server that sent headers and then stalled pinned the classification call. This is the
/// control for that defect: a body that never arrives must return quickly as Unavailable, not hang.
/// </remarks>
public sealed class NimbleStallingBodyTests
{
    [Fact]
    public async Task A_server_that_sends_headers_then_stalls_returns_unavailable_quickly()
    {
        var handler = new StallBodyHandler();
        var options = NimbleTestDoubles.Options();
        options.Timeout = TimeSpan.FromMilliseconds(250);

        var http = new HttpClient(handler);
        var classifier = new NimbleSemanticMailClassifier(http, options);

        var started = Stopwatch.GetTimestamp();
        var result = await classifier.ClassifyAsync(NimbleTestMessage.Input(), CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        // One dimension is NotApplicable by construction (no conversation window), so the claim is
        // the one that matters: nothing was reported as an answer.
        Assert.DoesNotContain(result.Evidence, row => row.Availability == EvidenceAvailability.Available);
        Assert.Contains(result.Evidence, row => row.Availability == EvidenceAvailability.Unavailable);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"the call took {elapsed}, so the deadline did not cover the body");
    }

    private sealed class StallBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() });
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.Delay(TimeSpan.FromMinutes(5));

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return 0;
        }
    }
}
