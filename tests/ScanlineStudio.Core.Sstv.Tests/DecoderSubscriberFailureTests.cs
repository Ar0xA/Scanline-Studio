using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class DecoderSubscriberFailureTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void AnalogLineDecoded_ThrowingFirstSubscriber_DoesNotInterruptOrDuplicateRows()
    {
        var samples = EncodeRobot36();
        using var decoder = new AnalogFmSstvDecoder(SampleRate);
        var expected = new InvalidOperationException("Injected direct line subscriber failure.");
        var rows = new List<int>();
        decoder.LineDecoded += _ => throw expected;
        decoder.LineDecoded += update => rows.Add(update.Line);

        var actual = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(samples));

        Assert.Same(expected, actual);
        Assert.True(rows.Count > 20, "Test setup problem: too few real lines decoded.");
        Assert.Equal(rows.Count, rows.Distinct().Count());
        Assert.True(rows.SequenceEqual(rows.Order()), "Rows must progress monotonically despite the first subscriber throwing.");

        var rowCount = rows.Count;
        decoder.PushSamples(new float[64]);
        Assert.Equal(rowCount, rows.Count);
    }

    [Fact]
    public void RestartableLineDecoded_ThrowingFirstSubscriber_DoesNotStarveSecondOrCorruptInner()
    {
        var samples = EncodeRobot36();
        using var decoder = new RestartableSstvDecoder(sampleRate: SampleRate);
        var expected = new InvalidOperationException("Injected wrapper line subscriber failure.");
        var rows = new List<int>();
        decoder.LineDecoded += _ => throw expected;
        decoder.LineDecoded += update => rows.Add(update.Line);

        var actual = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(samples));

        Assert.Same(expected, actual);
        Assert.True(rows.Count > 20, "Test setup problem: too few real lines decoded.");
        Assert.Equal(rows.Count, rows.Distinct().Count());
        Assert.True(rows.SequenceEqual(rows.Order()));
        Assert.Equal(0, decoder.RestartCountForTests);
    }

    [Fact]
    public void ModeDetected_ThrowingFirstSubscriber_CompletesCommitAndAttemptsLaterSubscriber()
    {
        using var decoder = new AnalogFmSstvDecoder(SampleRate);
        var expected = new InvalidOperationException("Injected mode subscriber failure.");
        var observed = new List<SstvModeDefinition>();
        decoder.ModeDetected += _ => throw expected;
        decoder.ModeDetected += observed.Add;
        decoder.ForceMode(SstvModeRegistry.Avt);

        var actual = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(new float[64]));

        Assert.Same(expected, actual);
        Assert.Single(observed);
        Assert.Equal(SstvModeRegistry.Avt.Id, observed[0].Id);
        Assert.Equal(SstvModeRegistry.Avt.Id, decoder.ModeForTests?.Id);
    }

    [Fact]
    public void Restartable_ReentrantPushFromDecodeEvent_IsRejectedBeforeItCanSwap()
    {
        var samples = EncodeRobot36();
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1);
        var laterSubscriberCount = 0;
        decoder.ModeDetected += _ => decoder.PushSamples(new float[1]);
        decoder.ModeDetected += _ => laterSubscriberCount++;

        var exception = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(samples));

        Assert.Contains("Recursive or concurrent", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, laterSubscriberCount);
        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.True(decoder.IsIdleForTests, "The outer full transmission should complete normally despite rejected reentry.");
    }

    [Fact]
    public void Analog_ReentrantPushFromDecodeEvent_IsRejectedWithoutInterruptingOuterCommit()
    {
        using var decoder = new AnalogFmSstvDecoder(SampleRate);
        var laterSubscriberCount = 0;
        decoder.ModeDetected += _ => decoder.PushSamples(new float[1]);
        decoder.ModeDetected += _ => laterSubscriberCount++;
        decoder.ForceMode(SstvModeRegistry.Avt);

        var exception = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(new float[64]));

        Assert.Contains("Recursive or concurrent", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, laterSubscriberCount);
        Assert.Equal(SstvModeRegistry.Avt.Id, decoder.ModeForTests?.Id);
        decoder.PushSamples(new float[64]);
    }

    [Fact]
    public void Restartable_InnerFailureWinsWhileEveryMaintenanceSubscriberIsAttempted()
    {
        var outgoing = new AnalogFmSstvDecoder();
        var disposedReplacement = new AnalogFmSstvDecoder();
        disposedReplacement.Dispose();
        var creationCount = 0;
        AnalogFmSstvDecoder CreateDecoder(int _) => ++creationCount == 1 ? outgoing : disposedReplacement;

        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1,
            decoderFactoryForTests: CreateDecoder);
        decoder.PushSamples(new float[16]);

        var notifications = new List<string>();
        decoder.RestartCriticallyOverdue += () => throw new IOException("maintenance critical");
        decoder.RestartCriticallyOverdue += () => notifications.Add("critical-later");
        decoder.Restarted += () => throw new IOException("maintenance restarted");
        decoder.Restarted += () => notifications.Add("restarted-later");

        Assert.Throws<ObjectDisposedException>(() => decoder.PushSamples(new float[1]));
        Assert.Equal(["critical-later", "restarted-later"], notifications);
    }

    [Fact]
    public void Restartable_WhenInnerSucceeds_RethrowsFirstMaintenanceFailureAfterAllSubscribers()
    {
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1);
        decoder.PushSamples(new float[1]);
        var expected = new InvalidOperationException("first maintenance failure");
        var notifications = new List<string>();
        decoder.RestartCriticallyOverdue += () => throw expected;
        decoder.RestartCriticallyOverdue += () => notifications.Add("critical-later");
        decoder.Restarted += () => throw new IOException("later maintenance failure");
        decoder.Restarted += () => notifications.Add("restarted-later");

        var actual = Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(new float[1]));

        Assert.Same(expected, actual);
        Assert.Equal(["critical-later", "restarted-later"], notifications);
    }

    [Fact]
    public void Restartable_ExtendedBufferLoggerFactory_ReachesInitialAndReplacementInner()
    {
        using var loggerFactory = new RecordingLoggerFactory();
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1,
            rxBufferMode: RxBufferMode.Extended,
            loggerFactory: loggerFactory);

        Assert.Equal(1, loggerFactory.RxDiskLoggerCreateCount);
        decoder.PushSamples(new float[1]);
        decoder.PushSamples(new float[1]);

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(2, loggerFactory.RxDiskLoggerCreateCount);
    }

    private static float[] EncodeRobot36()
    {
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var y = 0; y < mode.ImageHeight; y++)
        {
            for (var x = 0; x < mode.ImageWidth; x++)
            {
                pixels[y * mode.ImageWidth + x] = new Rgb24((byte)x, (byte)y, 128);
            }
        }

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(
            mode,
            new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels)).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public int RxDiskLoggerCreateCount { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName == typeof(RxDiskLineStagingBuffer).FullName)
            {
                RxDiskLoggerCreateCount++;
            }

            return PassiveLogger.Instance;
        }

        public void Dispose()
        {
        }
    }

    private sealed class PassiveLogger : ILogger
    {
        public static PassiveLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
