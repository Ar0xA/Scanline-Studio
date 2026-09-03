using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>CW-ID/FSK station-ID subsystem Phase 6's own planned deliverable (implementation plan,
/// Phase 6): one full end-to-end test using the REAL TX encoder and REAL RX decoder (not the fakes
/// every other layer's own tests use) -- operator A TX-encodes with FSK-ID + NR/RST enabled, the
/// real encoded audio is fed through the real <see cref="AnalogFmSstvDecoder"/>, and the resulting
/// <see cref="FskStationIdDecodedInfo"/> events are relayed (via <see cref="FakeSstvSessionService.RaiseStationIdDecoded"/>,
/// the same seam Phase 5's own unit tests already use to prove <see cref="RxImagePaneViewModel"/>'s
/// own auto-fill/self-filter logic in isolation) into a real <see cref="RxImagePaneViewModel"/>.
/// Proves the full Callsign+NR/RST decode shape survives the real wire encode/decode round trip
/// before it ever reaches the UI layer -- something no other test (DSP-level or UI-level alone)
/// covers.</summary>
public sealed class StationIdEndToEndTests
{
    private const int SampleRate = 11025;

    /// <summary>Same reasoning as <c>AnalogFmSstvEncoderStationIdWiringTests</c>'s own copy of this
    /// helper: any post-<c>EndOfImage</c> narrow-FSK scan needs trailing silence past
    /// <c>VisHeader.MaxSearchCeilingMs</c> to find anything at all.</summary>
    private static float[] PadPastFixedWindowCeiling(IEnumerable<float> samples)
    {
        var padded = samples.ToList();
        padded.AddRange(new float[SampleRate * 2]);
        return padded.ToArray();
    }

    private static async Task<List<float>> CollectAsync(IAsyncEnumerable<float> samples)
    {
        var list = new List<float>();
        await foreach (var sample in samples)
        {
            list.Add(sample);
        }

        return list;
    }

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, Enumerable.Repeat(new Rgb24(128, 64, 200), width * height).ToArray());

    /// <summary>fsk_cwid.md §A5: the real decoder stamps <c>decodedEvents</c> with its own real
    /// (nonzero) <c>ReceptionSequence</c>, so <see cref="RxImagePaneViewModel"/>'s stale guard needs
    /// a matching latch before those events are raised -- in production a real
    /// <see cref="ISstvSessionService.ModeDetected"/> always fires before <c>StationIdDecoded</c> for
    /// the same reception, latching <c>RxImagePaneViewModel._currentReceptionSequence</c> (see that
    /// field's own doc comment). Setting <see cref="FakeSstvSessionService.CurrentReceptionSequence"/>
    /// alone is NOT sufficient -- the VM only ever reads it synchronously from inside its own
    /// <c>OnModeDetected</c> handler, never lazily, so a real <c>ModeDetected</c> raise (plus a
    /// dispatcher pump to run the posted latch) is required here too.</summary>
    private static void LatchReceptionSequence(FakeSstvSessionService sstvSession, long receptionSequence)
    {
        sstvSession.CurrentReceptionSequence = receptionSequence;
        var mode = new SstvModeDefinition(
            Id: "mn73", DisplayName: "Martin M1 (narrow)", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Real-encode-then-real-decode: operator A's configured TX options, run through the
    /// same <see cref="AnalogFmSstvEncoder"/>/<see cref="AnalogFmSstvDecoder"/> production types
    /// used everywhere else, narrow mode (Mn73) to keep this test fast.</summary>
    private static async Task<List<FskStationIdDecodedInfo>> EncodeThenDecodeAsync(StationIdTransmitOptions stationId)
    {
        var mode = SstvModeRegistry.Mn73;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = PadPastFixedWindowCeiling(await CollectAsync(encoder.EncodeAsync(mode, image, stationId)));

        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var events = new List<FskStationIdDecodedInfo>();
        decoder.StationIdDecoded += info => events.Add(info);
        decoder.PushSamples(samples);

        return events;
    }

    [AvaloniaFact]
    public async Task RealEncodeThenDecode_DifferentOperator_AutoFillsCallsignAndNrRst()
    {
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            NrRstText = "599123", // filtered remainder "123" -- 3-digit compact-eligible form
        };
        var decodedEvents = await EncodeThenDecodeAsync(stationId);

        // Legacy sends the callsign packet and the NR/RST sub-packet as two distinct decode results
        // (NarrowFskHeaderDecoder.FskDecodeResult's own doc comment) -- expect exactly one of each,
        // not a single combined event.
        Assert.Contains(decodedEvents, e => e.Callsign == "W1AW");
        Assert.Contains(decodedEvents, e => e.CompactNr == 123);

        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "K2ABC" }; // operator B, different from A
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        LatchReceptionSequence(sstvSession, decodedEvents[0].ReceptionSequence);

        foreach (var info in decodedEvents)
        {
            sstvSession.RaiseStationIdDecoded(info);
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", vm.OverrideCallsign);
        Assert.Equal("595123", vm.DecodedNrRst);
    }

    [AvaloniaFact]
    public async Task RealEncodeThenDecode_OperatorsOwnCallsign_SelfFiltersCallsignButStillAppliesNrRst()
    {
        // Self-loopback: the RX operator's own callsign (exact, case-sensitive per
        // RxImagePaneViewModel.ApplyStationIdDecodedAsync's own doc comment) must NOT auto-fill
        // OverrideCallsign with itself -- the whole point of the self-filter. NR/RST has no
        // equivalent self-filter in that method (only the Callsign branch checks), so it still
        // applies even on a self-decode; asserted explicitly here since it's the one behavioral
        // asymmetry between the two branches, not an oversight in this test.
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            NrRstText = "599123",
        };
        var decodedEvents = await EncodeThenDecodeAsync(stationId);

        // Unlike the sibling test above (whose Assert.Contains calls already fail clearly on an
        // empty decode), this test has no earlier assert -- guard the indexer below explicitly so a
        // decode regression here surfaces as a clean failure, not an IndexOutOfRangeException.
        Assert.NotEmpty(decodedEvents);

        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" }; // same operator as TX
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        LatchReceptionSequence(sstvSession, decodedEvents[0].ReceptionSequence);

        foreach (var info in decodedEvents)
        {
            sstvSession.RaiseStationIdDecoded(info);
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
        Assert.Equal("595123", vm.DecodedNrRst);
    }
}
