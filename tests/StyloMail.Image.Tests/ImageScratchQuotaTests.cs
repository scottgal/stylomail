namespace StyloMail.Image.Tests;

public sealed class ImageScratchQuotaTests
{
    [Fact]
    public void RentReservesBeforeReturningABuffer()
    {
        var quota = new ImageScratchQuota(ImageObservationContract.PerMessageInspectedBytes);

        using var lease = quota.TryRent(ImageObservationContract.PerImageInspectedBytes);

        Assert.NotNull(lease);
        Assert.Equal(ImageObservationContract.PerImageInspectedBytes, lease.Length);
        Assert.Equal(ImageObservationContract.PerImageInspectedBytes, quota.ReservedBytes);
        Assert.Equal(lease.Length, lease.Buffer.Length);
    }

    [Fact]
    public void FailedReservationLeavesNoBufferAndNoCharge()
    {
        var quota = new ImageScratchQuota(ImageObservationContract.PerImageInspectedBytes - 1);

        var lease = quota.TryRent(ImageObservationContract.PerImageInspectedBytes);

        Assert.Null(lease);
        Assert.Equal(0, quota.ReservedBytes);
        Assert.Equal(quota.CapacityBytes, quota.AvailableBytes);
    }

    [Fact]
    public void ZeroCapacityNeverRents()
    {
        var quota = new ImageScratchQuota(0);

        Assert.Null(quota.TryRent(1));
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public void DisposeClearsTheBufferAndReturnsTheQuotaToItsPriorValue()
    {
        var quota = new ImageScratchQuota(ImageObservationContract.PerMessageInspectedBytes);
        var lease = quota.TryRent(ImageObservationContract.PerImageInspectedBytes);
        Assert.NotNull(lease);

        lease.Buffer.Span[0] = 0xFF;
        lease.Dispose();

        Assert.True(lease.IsDisposed);
        Assert.Equal(0, lease.Length);
        Assert.Equal(0, quota.ReservedBytes);
        Assert.Equal(quota.CapacityBytes, quota.AvailableBytes);
        Assert.Throws<ObjectDisposedException>(() => lease.Buffer);
    }

    [Fact]
    public void TwoLeasesCannotExceedCapacityAndReleaseRestoresIt()
    {
        var quota = new ImageScratchQuota(ImageObservationContract.PerMessageInspectedBytes);

        using var first = quota.TryRent(ImageObservationContract.PerImageInspectedBytes);
        using var second = quota.TryRent(ImageObservationContract.PerImageInspectedBytes);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(ImageObservationContract.PerMessageInspectedBytes, quota.ReservedBytes);

        Assert.Null(quota.TryRent(1));

        first.Dispose();
        Assert.Equal(ImageObservationContract.PerImageInspectedBytes, quota.ReservedBytes);

        second.Dispose();
        Assert.Equal(0, quota.ReservedBytes);
    }

    [Fact]
    public void ByteFloorRejectsNonPositiveRents()
    {
        var quota = new ImageScratchQuota(1024);

        Assert.Throws<ArgumentOutOfRangeException>(() => quota.TryRent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => quota.TryRent(-1));
    }
}
