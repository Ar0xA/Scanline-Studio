using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Auto-saves a received image to the RX history store on decode completion — see
/// spec/07-image-pipeline.md's "RX flow" section. Callsign is deliberately omitted from the
/// filename convention (legacy: timestamp + mode + optional callsign) — no QSO-entry UI exists yet
/// to supply one; <see cref="ReceiveHistoryEntry.LinkedQsoId"/> stays populatable but nothing sets
/// it in this pass.
///
/// <b>Completion detection — a real gap found during implementation, not in <see cref="ISstvDecoder"/>
/// itself:</b> there is no explicit "decode completed" event; only <see cref="ISstvDecoder.LineDecoded"/>
/// (fires per scanline *group* — one line for most families, two for paired-line families like
/// PD/MP/RM8/RM12 per <c>IScanlineDecoder.RowsPerTransmissionLine</c>, not exposed on the public
/// <see cref="SstvModeDefinition"/>). Rather than guessing the group size, this class *learns* it
/// from the first two <see cref="DecodedImageUpdate.Line"/> values observed for the current image
/// (their delta is exactly the group size, constant for the rest of that image) and only then
/// checks whether the group starting at <c>Line</c> is the image's last one — no completion check
/// happens at all before the step is learned (i.e. never on the very first event of an image). Every
/// real <see cref="SstvModeDefinition"/> has an <c>ImageHeight</c> far larger than one or two rows,
/// so an image that completes in a single event is not a real scenario worth guarding against
/// (deferring the check costs nothing in practice) — an earlier version of this class instead
/// defaulted the unlearned step to <c>ImageHeight</c> itself, which made the very first event of
/// *every* image look complete; caught by <c>ReceiveHistoryRecorderTests</c>, not by
/// review.</summary>
public sealed partial class ReceiveHistoryRecorder
{
    private readonly IReceivedImageBuffer _receivedImage;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ReceiveHistoryRecorder> _logger;

    private SstvModeDefinition? _currentMode;
    private int? _previousLine;
    private int? _observedStep;
    private bool _recordedForCurrentImage;

    public ReceiveHistoryRecorder(ISstvDecoder decoder, IReceivedImageBuffer receivedImage, IReceiveHistoryStore historyStore, ISettingsStore settingsStore, ILogger<ReceiveHistoryRecorder> logger)
    {
        _receivedImage = receivedImage;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _logger = logger;

        decoder.ModeDetected += OnModeDetected;
        decoder.LineDecoded += OnLineDecoded;
        decoder.DecodeRestarted += OnDecodeRestarted;
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        _currentMode = mode;
        _previousLine = null;
        _observedStep = null;
        _recordedForCurrentImage = false;
    }

    private void OnDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        // Deliberately not recorded: legacy's "save a >=65%-complete abandoned image" behavior
        // (sstv.cpp's m_ReqSave) is a separate, still-deferred feature (spec/14-roadmap.md) --
        // this class only ever records genuinely *completed* images.
        _recordedForCurrentImage = true;
    }

    private void OnLineDecoded(DecodedImageUpdate update)
    {
        if (_recordedForCurrentImage || _currentMode is not { } mode)
        {
            return;
        }

        if (_previousLine is int previousLine && _observedStep is null)
        {
            _observedStep = update.Line - previousLine;
        }

        _previousLine = update.Line;

        // The step (scanline group size) is only known from the SECOND event onward -- the first
        // event of any image can never be treated as "complete" here: every real SstvModeDefinition
        // has an ImageHeight far larger than one or two rows, so deferring the completion check
        // until the step is actually learned costs nothing in practice (see this class's own doc
        // comment) and avoids a real bug an early version of this method had: falling back to
        // `step = mode.ImageHeight` on the unlearned first event made `Line(0) + step >= ImageHeight`
        // trivially true, firing "complete" after just one line, every time.
        if (_observedStep is not int step || update.Line + step < mode.ImageHeight)
        {
            return;
        }

        _recordedForCurrentImage = true;
        var modeId = mode.Id;

        // Fire-and-forget, isolated -- must not block the caller (the audio drain thread, same
        // threading contract as SstvSessionService's own _decoderHandler/_waterfallHandler; disk +
        // SQLite I/O here would otherwise stall live decoding).
        _ = Task.Run(async () =>
        {
            try
            {
                await RecordCompletedImageAsync(modeId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not rethrown -- same reasoning as SstvSessionService's fan-out handlers: a failed
                // history save must never surface into/interrupt the live decode path. Now logged
                // (previously fully silent) -- this is exactly the "it didn't record my image" bug
                // class a user would otherwise have no way to diagnose.
                Log.RecordCompletedImageFailed(_logger, modeId, ex);
            }
        });
    }

    private async Task RecordCompletedImageAsync(string modeId)
    {
        var directory = await ResolveImagesDirectoryAsync().ConfigureAwait(false);
        Directory.CreateDirectory(directory);

        var receivedAt = DateTimeOffset.Now;
        var fileName = $"{receivedAt:yyyyMMdd-HHmmss}_{modeId}.png";
        var filePath = Path.Combine(directory, fileName);

        await _receivedImage.SaveAsync(filePath).ConfigureAwait(false);

        var entry = new ReceiveHistoryEntry(Guid.NewGuid().ToString(), receivedAt, modeId, filePath, LinkedQsoId: null);
        await _historyStore.RecordAsync(entry).ConfigureAwait(false);

        Log.ImageSaved(_logger, filePath);
    }

    private Task<string> ResolveImagesDirectoryAsync() => ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save completed RX image (mode={ModeId})")]
        public static partial void RecordCompletedImageFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX image saved: {FilePath}")]
        public static partial void ImageSaved(ILogger logger, string filePath);
    }
}
