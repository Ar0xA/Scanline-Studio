using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class AstraPrimarySyncVisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisDecode_FreezesPrimaryInterference_AndRejectedHeaderReturnsToSearch(bool rejectHeader)
    {
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var machine = decoder.PrimaryVisStateMachineForTests;
        var tracker = decoder.PrimarySyncTrackerForTests;
        var mode = SstvModeRegistry.Robot36;
        var vis = rejectHeader ? mode.VisCode ^ 1 : mode.VisCode;
        var segments = VisHeader.GenerateSegments(vis).Concat(new[] { (0.0, 500.0) });
        var agc = new LevelAgc(sampleRate);
        var phase = 0.0;
        var seeded = false;
        var returnedToSearch = false;
        var frozenSamples = 0;
        (SstvModeDefinition Mode, int LineStartSample)? locked = null;
        foreach (var (frequency, duration) in segments)
        {
            for (var i = 0; i < (int)Math.Round(duration * sampleRate / 1000); i++)
            {
                tracker.Increment();
                if (!machine.IsAtOrBeforeConfirmLock)
                {
                    if (!seeded)
                    {
                        SeedMatchingPeak(tracker, (int)Math.Round(mode.LineDurationMs * sampleRate / 1000));
                        seeded = true;
                    }
                    Assert.Null(decoder.TryStartPrimarySync());
                    var heldPeak = tracker.LastPeakPositionSamples;
                    // Controlled detector-envelope interference at the production primary gate.
                    decoder.UpdatePrimarySyncPeak(i % 2 == 0 ? 100000 + frozenSamples : 0, 0);
                    Assert.Equal(heldPeak, tracker.LastPeakPositionSamples);
                    frozenSamples++;
                }
                else if (seeded && machine.IsSearching)
                {
                    Assert.Equal(mode.Id, decoder.TryStartPrimarySync()?.Id);
                    Assert.Null(decoder.TryStartPrimarySync());
                    returnedToSearch = true;
                    break;
                }

                phase += 2 * Math.PI * frequency / sampleRate;
                var scaled = frequency == 0 ? 0 : (float)Math.Sin(phase) * 32768.0;
                agc.Do(scaled);
                agc.Fix();
                locked = machine.ProcessSample(Math.Clamp(agc.Agc(scaled) * 32, -16384, 16384));
                if (locked is not null) break;
            }
            if (locked is not null || returnedToSearch) break;
        }
        Assert.True(frozenSamples > 100);
        if (rejectHeader)
        {
            Assert.Null(locked);
            Assert.True(returnedToSearch);
        }
        else Assert.Equal(mode.Id, locked?.Mode.Id);
    }

    private static void SeedMatchingPeak(SyncIntervalTracker tracker, int interval)
    {
        tracker.Reset();
        for (var pulse = 0; pulse < 7; pulse++)
        {
            for (var i = 0; i < interval; i++) tracker.Increment();
            tracker.Trigger(100);
            if (pulse < 6) tracker.TryStart();
        }
    }
}
