using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Threading;
using StyloMail.Host.Hosting;
using StyloMail.Host.Proxy;
using StyloMail.Host.Storage;
using StyloMail.Image;
using StyloMail.Mime;

namespace StyloMail.Host.Tests;

public sealed class ImageProducerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private const string Tenant = "acme";
    private const string Account = "pa_account";

    [Fact]
    public async Task LogoOnePixelAndQrAdmitTheQrAndNotThePixel()
    {
        var raw = Message(
            ("logo.png", Png(800, 600)),
            ("pixel.png", Png(1, 1)),
            ("qr.png", Png(200, 200)));
        var sink = new RecordingSink();
        var quota = new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes);

        var analysis = await AnalyzeAsync(raw, sink, quota);

        Assert.NotNull(analysis);
        Assert.NotNull(sink.Observation);
        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        Assert.Equal(3, observation.ImageParts);
        Assert.Equal(1, observation.ImagesSkippedAsChrome);
        Assert.Equal(2, observation.ImagesAdmitted);
        Assert.Equal(2, observation.ImagesProduced);
        Assert.Equal(0, observation.ImagesOverBudget);
        Assert.Equal(2, observation.Parts.Count);
        Assert.Contains(observation.Parts, part => part.Ordinal == 3);
        Assert.Contains(observation.Parts, part => part.Ordinal == 5);
        Assert.True(observation.EvidenceRowCount <= 13);
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public async Task ExifHeavyJpegIsReadAtTheCallSiteLimit()
    {
        var exif = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "exif-heavy.jpg"));
        var raw = BinaryMessage(exif);
        Assert.True(raw.Length < 256 * 1024);
        var sink = new RecordingSink();
        var analysis = await AnalyzeAsync(
            raw, sink, new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes));

        Assert.NotNull(analysis);
        Assert.NotNull(sink.Observation);
        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        var part = Assert.Single(observation.Parts);
        Assert.Equal(3, part.Width);
        Assert.Equal(2, part.Height);
        Assert.Equal(ProxyImageDisposition.Inspected, part.Disposition);
    }

    [Fact]
    public async Task QuotaDenialAllocatesNothingAndWritesNothing()
    {
        var raw = Message(("qr.png", Png(200, 200)));
        var sink = new RecordingSink();
        var quota = new ImageScratchQuota(0);

        await AnalyzeAsync(raw, sink, quota);

        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        Assert.Equal(1, observation.ImagesAdmitted);
        Assert.Equal(0, observation.ImagesSkippedAsChrome);
        Assert.Equal(0, observation.ImagesProduced);
        Assert.Equal(1, observation.ImagesNotProduced);
        Assert.Equal(ProxyImageReason.QuotaDenied, Assert.Single(observation.Parts).AvailabilityReason);
        Assert.Equal(ProxyImageAvailability.Unavailable, observation.Availability);
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public async Task QuotaDenialDoesNotClassifySmallUnprobedImagesAsChrome()
    {
        var raw = Message(("qr1.png", Png(200, 200)), ("qr2.png", Png(200, 200)));
        var sink = new RecordingSink();
        var quota = new ImageScratchQuota(0);

        await AnalyzeAsync(raw, sink, quota);

        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        Assert.Equal(2, observation.ImageParts);
        Assert.Equal(0, observation.ImagesSkippedAsChrome);
        Assert.Equal(2, observation.ImagesAdmitted);
        Assert.Equal(0, observation.ImagesProduced);
        Assert.Equal(2, observation.ImagesNotProduced);
        Assert.Equal(2, observation.Parts.Count);
        Assert.All(observation.Parts, part => Assert.Equal(ProxyImageReason.QuotaDenied, part.AvailabilityReason));
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public async Task QuotaDenialWithManyImagesKeepsTwoPartRecordsAndHonestOverflow()
    {
        var images = Enumerable.Range(1, 128)
            .Select(index => ($"d{index}.png", Png(200, 200)))
            .ToArray();
        var raw = Message(images);
        var sink = new RecordingSink();

        await AnalyzeAsync(raw, sink, new ImageScratchQuota(0));

        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        Assert.Equal(128, observation.ImageParts);
        Assert.Equal(0, observation.ImagesSkippedAsChrome);
        Assert.Equal(2, observation.ImagesAdmitted);
        Assert.Equal(2, observation.Parts.Count);
        Assert.Equal(126, observation.ImagesOverBudget);
        Assert.Equal(1, observation.EvidenceRowCount);
    }

    [Fact]
    public async Task DisabledDoesNoKeyDerivationWorkOrWrites()
    {
        var raw = Message(("logo.png", Png(800, 600)));
        var sink = new RecordingSink();
        var secrets = new CountingSecrets("fixture-profile-key-32-bytes-long!!");
        var quota = new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes);

        await AnalyzeAsync(raw, sink, quota, secrets, enabled: false);

        Assert.Null(sink.Observation);
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public async Task NoScratchContextDoesNotObserveEvenWhenEnabled()
    {
        var raw = Message(("logo.png", Png(800, 600)));
        var sink = new RecordingSink();
        var secrets = new CountingSecrets("fixture-profile-key-32-bytes-long!!");

        await AnalyzeAsync(raw, sink, quota: null, secrets);

        Assert.Null(sink.Observation);
        Assert.Equal(0, secrets.Reads);
    }

    [Fact]
    public async Task PartialHashedImageKeepsUnknownTrailingCoverage()
    {
        var raw = Message(("large.png", Png(200, 200, payloadBytes: 3000)));
        var sink = new RecordingSink();

        await AnalyzeAsync(
            raw, sink,
            new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes),
            mimeLimits: new MimeParseLimits { MaxAttachmentHashBytes = 1024 });

        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        var part = Assert.Single(observation.Parts);
        Assert.Null(part.TrailingByteCount);
        Assert.Equal(ProxyImageAvailability.ReducedCoverage, part.Availability);
        Assert.Equal(ProxyImageAvailability.ReducedCoverage, observation.Availability);
    }

    [Fact]
    public async Task MissingKeyFailsClosed()
    {
        var raw = Message(("logo.png", Png(800, 600)));
        var sink = new RecordingSink();
        var secrets = new CountingSecrets(null);

        await AnalyzeAsync(raw, sink,
            new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes), secrets);

        Assert.Null(sink.Observation);
    }

    [Fact]
    public void TenantSeparationAndMissingKey()
    {
        var identity = new ImageObservationIdentity(new CountingSecrets("fixture-profile-key-32-bytes-long!!"));
        var bytes = Png(10, 10);
        Assert.True(identity.TryCreate(Tenant, Account, "Retrieval", "m-1", bytes, out var acme));
        Assert.True(identity.TryCreate("globex", Account, "Retrieval", "m-1", bytes, out var globex));

        Assert.NotEqual(acme.ObservationKey, globex.ObservationKey);
        Assert.NotEqual(acme.ContentFingerprint, globex.ContentFingerprint);

        Assert.True(identity.TryCreate(Tenant, "other-account", "Retrieval", "m-1", bytes, out var otherAccount));
        Assert.NotEqual(acme.ObservationKey, otherAccount.ObservationKey);
        Assert.True(identity.TryCreate(Tenant, Account, "Submission", "m-1", bytes, out var submission));
        Assert.NotEqual(acme.ObservationKey, submission.ObservationKey);

        var rawDigest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.NotEqual(rawDigest, acme.ContentFingerprint);

        acme.Dispose();
        globex.Dispose();
        otherAccount.Dispose();
        submission.Dispose();

        var missing = new ImageObservationIdentity(new CountingSecrets(null));
        Assert.False(missing.TryCreate(Tenant, Account, "Retrieval", "m-1", bytes, out _));
    }

    [Fact]
    public async Task ManyImagePartsAreBoundedToTwoAdmissions()
    {
        var images = Enumerable.Range(1, 128)
            .Select(index => ($"x{index}.png", Png(40, 40, payloadBytes: 3000)))
            .ToArray();
        var raw = Message(images);
        var sink = new RecordingSink();

        await AnalyzeAsync(raw, sink,
            new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes));

        Assert.NotNull(sink.Observation);
        var observation = Assert.IsType<ProxyImageObservation>(sink.Observation);
        Assert.Equal(128, observation.ImageParts);
        Assert.Equal(2, observation.ImagesAdmitted);
        Assert.Equal(126, observation.ImagesOverBudget);
        Assert.Equal(2, observation.Parts.Count);
        Assert.Equal(ProxyImageAvailability.ReducedCoverage, observation.Availability);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static async Task<ProxyWarningAnalysisResult?> AnalyzeAsync(
        byte[] raw,
        RecordingSink sink,
        IImageScratchQuota? quota,
        IAssessmentSecretSource? secrets = null,
        bool enabled = true,
        MimeParseLimits? mimeLimits = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stylomail-image-producer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new HostDatabase(Options.Create(new HostStorageOptions
            {
                DatabasePath = Path.Combine(directory, "host.db"),
            }));
            database.EnsureCreated();
            var store = new SqliteProxyImageStore(database, Options.Create(new HostProxyOptions()));
            Assert.True(await store.InitializeEpochAsync());

            var analyzer = new HostProxyWarningAnalyzer(
                new BoundedMimeMessageAnalyzer(),
                new ThreadingHeaderExtractor(new StableTokenDeriver()),
                TimeProvider.System,
                new HostImageOptions { Enabled = enabled },
                new ImageObservationIdentity(secrets ?? new CountingSecrets("fixture-profile-key-32-bytes-long!!")),
                sink,
                store,
                mimeLimits);

            var result = await analyzer.AnalyzeAsync(new ProxyWarningAnalysisInput
            {
                Message = raw,
                TenantId = Tenant,
                AccountId = Account,
                ObservedAt = Now,
                ImageScratch = quota,
            }, CancellationToken.None);
            return result;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Message(params (string Name, byte[] Bytes)[] images)
    {
        var builder = new StringBuilder();
        builder.Append("From: Alice <alice@example.com>\r\n");
        builder.Append("To: Bob <bob@example.org>\r\n");
        builder.Append("Subject: image producer\r\n");
        builder.Append("Date: Mon, 18 Oct 2026 12:00:00 +0000\r\n");
        builder.Append("Message-ID: <producer@example.com>\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: multipart/mixed; boundary=\"img\"\r\n\r\n");
        builder.Append("--img\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nbody\r\n\r\n");

        foreach (var image in images)
        {
            builder.Append("--img\r\n");
            var contentType = image.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg"
                : "image/png";
            builder.Append("Content-Type: ").Append(contentType).Append("; name=\"").Append(image.Name).Append("\"\r\n");
            builder.Append("Content-Disposition: attachment; filename=\"").Append(image.Name).Append("\"\r\n");
            builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            builder.Append(Convert.ToBase64String(image.Bytes, Base64FormattingOptions.InsertLineBreaks));
            builder.Append("\r\n\r\n");
        }

        builder.Append("--img--\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BinaryMessage(byte[] image)
    {
        using var stream = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            "From: Alice <alice@example.com>\r\n"
            + "To: Bob <bob@example.org>\r\n"
            + "Subject: binary image producer\r\n"
            + "Date: Mon, 18 Oct 2026 12:00:00 +0000\r\n"
            + "Message-ID: <binary-producer@example.com>\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=\"bin\"\r\n\r\n"
            + "--bin\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nbody\r\n\r\n"
            + "--bin\r\nContent-Type: image/jpeg; name=\"exif.jpg\"\r\n"
            + "Content-Disposition: attachment; filename=\"exif.jpg\"\r\n"
            + "Content-Transfer-Encoding: binary\r\n\r\n");
        stream.Write(header);
        stream.Write(image);
        stream.Write(Encoding.ASCII.GetBytes("\r\n--bin--\r\n"));
        return stream.ToArray();
    }

    private static byte[] Png(int width, int height, int payloadBytes = 0)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(stream, "IHDR"u8, ihdr);
        if (payloadBytes > 0)
        {
            WriteChunk(stream, "IDAT"u8, new byte[payloadBytes]);
        }
        WriteChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        var crc = Crc32(type, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
        {
            crc = Update(crc, value);
        }

        foreach (var value in second)
        {
            crc = Update(crc, value);
        }

        return ~crc;

        static uint Update(uint crc, byte value)
        {
            crc ^= value;
            for (var index = 0; index < 8; index++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc;
        }
    }

    private sealed class RecordingSink : IProxyImageObservationSink
    {
        public ProxyImageObservation? Observation { get; private set; }

        public ValueTask RecordAsync(ProxyImageObservation observation, CancellationToken cancellationToken)
        {
            Observation = observation;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSecrets(string? profileKey) : IAssessmentSecretSource
    {
        public int Reads { get; private set; }

        public AssessmentSecrets Read()
        {
            Reads++;
            return new AssessmentSecrets(null, profileKey);
        }
    }

    private sealed class StableTokenDeriver : IThreadTokenDeriver
    {
        public string Derive(string tenantId, string value)
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(tenantId + "\0" + value)));
        }
    }
}
