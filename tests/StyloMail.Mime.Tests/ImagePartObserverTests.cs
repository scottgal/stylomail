using System.Collections;
using System.Text;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public sealed class ImagePartObserverTests
{
    private static readonly DateTimeOffset ObservedAt = FixtureMessage.ReceivedAt;

    [Fact]
    public void ObserverReceivesTheImagePartAndRowsAreAppended()
    {
        var image = ImageBytes();
        var raw = ImageMessage.WithImages(("image/png", "x.png", image, "base64"));
        var observer = new RecordingObserver();
        ImagePartSource? retained = null;

        observer.OnComplete = parts =>
        {
            Assert.True(parts.Count == 1,
                $"expected one image part, got {string.Join(", ", parts.Select(p => $"{p.Ordinal}:{p.DeclaredContentType}:{p.ObservedDecodedBytes}"))}");
            var part = Assert.Single(parts);
            retained = part;
            Assert.True(part.IsValid);
            Assert.Equal(3, part.Ordinal);
            Assert.Equal("image/png", part.DeclaredContentType);
            Assert.Equal(image.Length, part.ObservedDecodedBytes);
            Assert.True(part.DecodedLengthIsComplete);
            Assert.Equal(image.Length, part.KnownDecodedLength);

            // The opener must not consume or drop the first byte.
            Assert.True(part.TryOpenContent(out var firstStream));
            using (var stream = firstStream!)
            {
                var first = new byte[1];
                Assert.Equal(1, stream.Read(first, 0, 1));
                Assert.Equal(0x89, first[0]);
                using var rest = new MemoryStream();
                stream.CopyTo(rest);
                Assert.Equal(image.Length, rest.Length + 1);
            }

            // A second sequential open returns the full decoded content again.
            Assert.True(part.TryOpenContent(out var secondStream));
            using (var stream = secondStream!)
            {
                using var all = new MemoryStream();
                stream.CopyTo(all);
                Assert.Equal(image, all.ToArray());
            }

            return [Row()];
        };

        var result = Analyze(raw, observer);

        Assert.Null(observer.Failure);
        Assert.Equal(1, observer.Calls);
        Assert.NotNull(observer.Parts);
        Assert.Single(observer.Parts!);
        Assert.Contains(result.Evidence, e => e.SignalId == "deterministic.image_inspection" && e.SourceVersion == "stylomail-image/2");
        Assert.NotNull(retained);
        Assert.False(retained!.IsValid);
        Assert.False(retained.TryOpenContent(out _));
    }

    [Fact]
    public void QuotedPrintableImageIsCollectedAndReopened()
    {
        var image = ImageBytes();
        var raw = ImageMessage.WithImages(("image/png", "x.png", image, "quoted-printable"));
        var observer = new RecordingObserver();

        observer.OnComplete = parts =>
        {
            var part = Assert.Single(parts);
            Assert.Equal(image.Length, part.KnownDecodedLength);

            for (var open = 0; open < 2; open++)
            {
                Assert.True(part.TryOpenContent(out var stream));
                using (stream)
                {
                    using var all = new MemoryStream();
                    stream!.CopyTo(all);
                    Assert.Equal(image, all.ToArray());
                }
            }

            return [];
        };

        var result = Analyze(raw, observer);

        Assert.Equal(1, observer.Calls);
        Assert.DoesNotContain(result.Evidence, e => e.SignalId.StartsWith("deterministic.image_", StringComparison.Ordinal));
    }

    [Fact]
    public void NullObserverLeavesTheBaselineEvidenceUnchanged()
    {
        var raw = ImageMessage.WithImages(("image/png", "x.png", ImageBytes(), "base64"));
        var baseline = Analyze(raw, observer: null);
        var emptyObserver = new RecordingObserver();

        var observed = Analyze(raw, emptyObserver);

        Assert.Equal(1, emptyObserver.Calls);
        Assert.Equal(baseline.Evidence.Select(e => e.SignalId), observed.Evidence.Select(e => e.SignalId));
        Assert.Equal(baseline.Evidence.Count, observed.Evidence.Count);
    }

    [Fact]
    public void ThrowingObserverPreservesBaselineEvidenceAtomically()
    {
        var raw = ImageMessage.WithImages(("image/png", "x.png", ImageBytes(), "base64"));
        var baseline = Analyze(raw, observer: null);
        var observer = new RecordingObserver();
        ImagePartSource? retained = null;

        observer.OnComplete = parts =>
        {
            retained = parts[0];
            throw new InvalidOperationException("observer failure");
        };

        var result = Analyze(raw, observer);

        Assert.Equal(baseline.Evidence.Select(e => e.SignalId), result.Evidence.Select(e => e.SignalId));
        Assert.NotNull(retained);
        Assert.False(retained!.IsValid);
        Assert.False(retained.TryOpenContent(out _));
    }

    [Fact]
    public void OversizedRowSetIsDroppedAtomically()
    {
        var raw = ImageMessage.WithImages(("image/png", "x.png", ImageBytes(), "base64"));
        var baseline = Analyze(raw, observer: null);
        var observer = new RecordingObserver
        {
            OnComplete = _ => Enumerable.Range(0, 14).Select(_ => Row()).ToArray(),
        };

        var result = Analyze(raw, observer);

        Assert.Equal(baseline.Evidence.Select(e => e.SignalId), result.Evidence.Select(e => e.SignalId));
        Assert.DoesNotContain(result.Evidence, e => e.SignalId.StartsWith("deterministic.image_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("semantic.not_an_image", EvidenceOrigin.Deterministic)]
    [InlineData("deterministic.image_inspection", EvidenceOrigin.Semantic)]
    [InlineData("", EvidenceOrigin.Deterministic)]
    public void InvalidRowsAreDroppedAtomically(string signalId, EvidenceOrigin origin)
    {
        var raw = ImageMessage.WithImages(("image/png", "x.png", ImageBytes(), "base64"));
        var baseline = Analyze(raw, observer: null);
        var observer = new RecordingObserver
        {
            OnComplete = _ => [Row(signalId, origin)],
        };

        var result = Analyze(raw, observer);

        Assert.Equal(baseline.Evidence.Select(e => e.SignalId), result.Evidence.Select(e => e.SignalId));
    }

    [Fact]
    public void EnumeratingTheObserverResultIsFailureAtomic()
    {
        var raw = ImageMessage.WithImages(("image/png", "x.png", ImageBytes(), "base64"));
        var baseline = Analyze(raw, observer: null);
        var observer = new RecordingObserver { OnComplete = _ => new ThrowingRows() };

        var result = Analyze(raw, observer);

        Assert.Equal(baseline.Evidence.Select(e => e.SignalId), result.Evidence.Select(e => e.SignalId));
    }

    [Fact]
    public void DescriptorsAreCollectedBeyondTheAttachmentResultCap()
    {
        var images = Enumerable.Range(1, 130)
            .Select(index => ("image/png", $"x{index}.png", ImageBytes(), "base64"))
            .ToArray();
        var raw = ImageMessage.WithImages(images);
        var observer = new RecordingObserver();

        observer.OnComplete = parts =>
        {
            Assert.Equal(130, parts.Count);
            Assert.Equal(parts.Count, parts.Select(p => p.Ordinal).Distinct().Count());
            Assert.All(parts, p => Assert.True(p.Ordinal > 0));
            return [];
        };

        var result = Analyze(raw, observer);

        Assert.Equal(1, observer.Calls);
        Assert.Equal(130, observer.Parts!.Count);
        Assert.Equal(128, result.Message!.Attachments.Count);
    }

    [Fact]
    public void UnavailableImageIsNotOffered()
    {
        var raw = ImageMessage.WithImages(("application/pgp-encrypted", "x.png", ImageBytes(), "base64"));
        var observer = new RecordingObserver();

        var result = Analyze(raw, observer);

        Assert.Equal(1, observer.Calls);
        Assert.Empty(observer.Parts!);
        Assert.DoesNotContain(result.Evidence, e => e.SignalId.StartsWith("deterministic.image_", StringComparison.Ordinal));
    }

    [Fact]
    public void ObserverIsCalledOnceWithAnEmptyListWhenThereAreNoImages()
    {
        var raw = SyntheticMessage.WithBody("no images here");
        var observer = new RecordingObserver();

        Analyze(raw, observer);

        Assert.Equal(1, observer.Calls);
        Assert.Empty(observer.Parts!);
    }

    [Fact]
    public async Task ConcurrentAnalysesUseIndependentObservers()
    {
        var first = new RecordingObserver { OnComplete = _ => [Row()] };
        var second = new RecordingObserver { OnComplete = _ => [Row()] };

        var firstTask = Task.Run(() => Analyze(
            ImageMessage.WithImages(("image/png", "a.png", ImageBytes(), "base64")), first));
        var secondTask = Task.Run(() => Analyze(
            ImageMessage.WithImages(("image/jpeg", "b.jpg", ImageBytes(), "base64")), second));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Single(first.Parts!);
        Assert.Single(second.Parts!);
        Assert.All(results, result => Assert.Contains(
            result.Evidence,
            e => e.SignalId == "deterministic.image_inspection"));
    }

    private static MimeAnalysisResult Analyze(byte[] raw, IImagePartObserver? observer) =>
        new BoundedMimeMessageAnalyzer().Analyze(new MimeAnalysisRequest
        {
            Envelope = new MailEnvelope
            {
                InternalMessageId = "msg-image-seam",
                TenantId = "tenant-test",
                Direction = MailDirection.Inbound,
                TrustedPrincipalId = "connector-test",
                MailFrom = "sender@example.com",
                RcptTo = ["bob@example.org"],
                ReceivedAt = ObservedAt,
                MimeDigest = "sha256:test",
                PayloadReference = "spool://test/image-seam",
            },
            RawMessage = raw,
            TimeProvider = new FixedTimeProvider(ObservedAt),
            ImageParts = observer,
        });

    private static Evidence Row(
        string signalId = "deterministic.image_inspection",
        EvidenceOrigin origin = EvidenceOrigin.Deterministic) => new()
        {
            SignalId = signalId,
            Origin = origin,
            Availability = EvidenceAvailability.Available,
            Value = null,
            Confidence = null,
            SampleSupport = null,
            SourceVersion = "stylomail-image/2",
            ObservedAt = ObservedAt,
            ObservedScope = "message",
            Attributes = null,
        };

    private static byte[] ImageBytes()
    {
        var bytes = new byte[96];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 37 % 251);
        }

        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        return bytes;
    }

    private sealed class RecordingObserver : IImagePartObserver
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ImagePartSource>? Parts { get; private set; }

        public Func<IReadOnlyList<ImagePartSource>, IReadOnlyList<Evidence>>? OnComplete { get; set; }

        public Exception? Failure { get; private set; }

        public IReadOnlyList<Evidence> Complete(IReadOnlyList<ImagePartSource> parts)
        {
            Calls++;
            Parts = parts;
            try
            {
                return OnComplete?.Invoke(parts) ?? [];
            }
            catch (Exception ex)
            {
                Failure = ex;
                throw;
            }
        }
    }

    private sealed class ThrowingRows : IReadOnlyList<Evidence>
    {
        public Evidence this[int index] => throw new InvalidOperationException("enumeration failure");

        public int Count => 1;

        public IEnumerator<Evidence> GetEnumerator() => throw new InvalidOperationException("enumeration failure");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Builds multipart/mixed messages with image attachments for the observer seam.</summary>
internal static class ImageMessage
{
    public static byte[] WithImages(params (string ContentType, string FileName, byte[] Bytes, string Encoding)[] images)
    {
        var builder = new StringBuilder();
        builder.Append(SyntheticMessage.BaselineHeaders).Append("\r\n");
        builder.Append("Content-Type: multipart/mixed; boundary=\"img\"\r\n\r\n");

        builder.Append("--img\r\n");
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        builder.Append("body\r\n\r\n");

        foreach (var image in images)
        {
            builder.Append("--img\r\n");
            builder.Append("Content-Type: ").Append(image.ContentType)
                .Append("; name=\"").Append(image.FileName).Append("\"\r\n");
            builder.Append("Content-Disposition: attachment; filename=\"").Append(image.FileName).Append("\"\r\n");
            builder.Append("Content-Transfer-Encoding: ").Append(image.Encoding).Append("\r\n\r\n");
            builder.Append(image.Encoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToBase64String(image.Bytes, Base64FormattingOptions.InsertLineBreaks)
                : QuotedPrintable(image.Bytes));
            builder.Append("\r\n\r\n");
        }

        builder.Append("--img--\r\n");
        return SyntheticMessage.Utf8(builder.ToString());
    }

    private static string QuotedPrintable(byte[] data)
    {
        var builder = new StringBuilder();
        var lineLength = 0;

        foreach (var value in data)
        {
            var token = value is >= 33 and <= 126 && value != (byte)'='
                ? ((char)value).ToString()
                : $"={value:X2}";

            if (lineLength + token.Length > 75)
            {
                builder.Append("=\r\n");
                lineLength = 0;
            }

            builder.Append(token);
            lineLength += token.Length;
        }

        return builder.ToString();
    }
}
