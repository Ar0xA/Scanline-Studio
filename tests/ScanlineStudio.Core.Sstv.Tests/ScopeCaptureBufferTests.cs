namespace ScanlineStudio.Core.Sstv.Tests;

public class ScopeCaptureBufferTests
{
    [Fact]
    public void UnarmedBuffer_WriteIsANoOp_NeverFills()
    {
        var buffer = new ScopeCaptureBuffer();

        buffer.Write(1.0);
        buffer.Write(2.0);

        Assert.False(buffer.IsFull);
        Assert.Null(buffer.TrySnapshot());
    }

    [Fact]
    public void IsCapturing_DistinguishesUnarmedFromMidCaptureFromFull()
    {
        // The load-bearing distinction: !IsFull is TRUE for both "never armed" and "mid-capture" --
        // a caller deciding whether extra work is worth doing needs IsCapturing instead.
        var buffer = new ScopeCaptureBuffer();
        Assert.False(buffer.IsCapturing); // never armed

        buffer.Arm(2);
        Assert.True(buffer.IsCapturing); // armed, not yet full

        buffer.Write(1.0);
        buffer.Write(2.0);
        Assert.False(buffer.IsCapturing); // full
        Assert.True(buffer.IsFull);
    }

    [Fact]
    public void Arm_ThenWriteExactlySize_FillsAndPublishesTheExactValuesInOrder()
    {
        var buffer = new ScopeCaptureBuffer();
        buffer.Arm(4);

        buffer.Write(10.0);
        buffer.Write(20.0);
        buffer.Write(30.0);
        Assert.False(buffer.IsFull);

        buffer.Write(40.0);

        Assert.True(buffer.IsFull);
        Assert.Equal([10.0, 20.0, 30.0, 40.0], buffer.TrySnapshot());
    }

    [Fact]
    public void Write_PastFull_IsANoOp_DoesNotThrowOrOverwrite()
    {
        var buffer = new ScopeCaptureBuffer();
        buffer.Arm(2);
        buffer.Write(1.0);
        buffer.Write(2.0);
        Assert.True(buffer.IsFull);

        buffer.Write(999.0);

        Assert.Equal([1.0, 2.0], buffer.TrySnapshot());
    }

    [Fact]
    public void Rearming_DiscardsAPartialCapture_AndResetsIsFull()
    {
        var buffer = new ScopeCaptureBuffer();
        buffer.Arm(4);
        buffer.Write(1.0);
        buffer.Write(2.0);

        buffer.Arm(3);

        Assert.False(buffer.IsFull);
        Assert.Null(buffer.TrySnapshot());
        buffer.Write(5.0);
        buffer.Write(6.0);
        buffer.Write(7.0);
        Assert.Equal([5.0, 6.0, 7.0], buffer.TrySnapshot());
    }

    [Fact]
    public void Rearming_DiscardsACompletedButUnreadCapture()
    {
        // Legacy's TrigNext has no "already armed"/"already full" guard -- a re-trigger always
        // starts fresh, even over an unread completed capture.
        var buffer = new ScopeCaptureBuffer();
        buffer.Arm(1);
        buffer.Write(42.0);
        Assert.True(buffer.IsFull);

        buffer.Arm(1);

        Assert.False(buffer.IsFull);
        Assert.Null(buffer.TrySnapshot());
    }

    [Fact]
    public void TwoIndependentBuffers_DoNotShareState()
    {
        // Per-channel independent readiness -- see the class's own doc comment (legacy's m_DataFlag
        // is per-CScope instance, not one shared gate).
        var channel0 = new ScopeCaptureBuffer();
        var channel1 = new ScopeCaptureBuffer();
        channel0.Arm(2);
        channel1.Arm(2);

        channel0.Write(1.0);
        channel0.Write(2.0);

        Assert.True(channel0.IsFull);
        Assert.False(channel1.IsFull);
        Assert.Null(channel1.TrySnapshot());
    }

    [Fact]
    public void Arm_WithSizeZero_IsImmediatelyFull_WithAnEmptySnapshot()
    {
        var buffer = new ScopeCaptureBuffer();

        buffer.Arm(0);

        // A zero-length target has nothing to write, so it should never look "still capturing" --
        // Write itself never gets a chance to observe count>=length and publish, so Arm must handle
        // this edge case directly rather than leaving IsFull permanently false for a 0-size arm.
        Assert.True(buffer.IsFull);
        Assert.Equal([], buffer.TrySnapshot());
    }
}
