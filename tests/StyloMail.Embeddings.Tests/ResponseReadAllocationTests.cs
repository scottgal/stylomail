using System.Net;
using System.Reflection;
using System.Text;
using Xunit.Abstractions;

namespace StyloMail.Embeddings.Tests;

public sealed class ResponseReadAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void A_large_streamed_body_is_decoded_without_a_second_full_byte_array()
    {
        // Exercise the transport independently of JSON parsing and token-file I/O. The memory
        // stream completes synchronously, so this thread's allocation count covers the whole read.
        var expected = new string('x', 1024 * 1024 - 8) + "é🙂";
        var bytes = Encoding.UTF8.GetBytes(expected);
        using var http = new HttpClient(new BodyHandler(bytes));
        var provider = new SegmentEmbeddingProvider(http, new EmbeddingOptions());
        var send = typeof(SegmentEmbeddingProvider)
            .GetMethod("SendAndReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Func<HttpRequestMessage>, CancellationToken, Task<(HttpStatusCode Status, string Body)>>>(provider);
        Func<HttpRequestMessage> request = () => new(HttpMethod.Get, "http://localhost/allocation-fixture");

        for (var i = 0; i < 3; i++) Read();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 3; i++) Read();
        var allocatedPerRead = (GC.GetAllocatedBytesForCurrentThread() - before) / 3;
        output.WriteLine($"Bytes allocated per ~1 MiB response: {allocatedPerRead}");

        // Buffer growth plus UTF-16 output costs about 4 MiB. A ToArray copy adds another MiB.
        Assert.True(allocatedPerRead < 4_700_000, $"Response allocated {allocatedPerRead} bytes");

        void Read()
        {
            var task = send(request, CancellationToken.None);
            Assert.True(task.IsCompletedSuccessfully, "The allocation fixture must stay on one thread.");
            var result = task.GetAwaiter().GetResult();
            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.Equal(expected, result.Body);
        }
    }

    private sealed class BodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(body, writable: false)),
            });
    }
}
