using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>A received DRM file after storage.</summary>
/// <param name="File">The file as received.</param>
/// <param name="RawPath">Where the raw bytes were written, inside the DRM files folder.</param>
/// <param name="HistoryEntry">The gallery/history row when the file decoded as an image; else null.</param>
public sealed record DrmStoredFile(DrmReceivedFile File, string RawPath, ReceiveHistoryEntry? HistoryEntry);

/// <summary>Stores every received DRM file. Subscribe here, not to <see cref="ISstvSessionService.DrmFileReceived"/>,
/// to see stored files.</summary>
public interface IDrmReceptionRecorder
{
    /// <summary>Raised after each file is stored raw (with its history row when it is an image and that succeeded), on
    /// the save task's thread (a pool thread), never on the DRM worker. Saves run one at a time, so these arrive in
    /// receive order; a slow subscriber delays the next save and the shutdown drain, never reception.</summary>
    event Action<DrmStoredFile>? FileStored;

    /// <summary>Stops admitting files and waits for queued saves, bounded (default 30 s). False when a save failed or
    /// the bound expired. Repeated calls share one drain.</summary>
    Task<bool> DrainAsync(TimeSpan? timeout = null);
}

/// <summary>Writes every received DRM file raw into the DRM files folder, never overwriting; an image also gets a PNG in
/// the images folder and a history row (mode "drm", the FAC callsign). The event handler only enqueues: the worker or
/// file-decode thread is never blocked by I/O.</summary>
public sealed partial class DrmReceptionRecorder : IDrmReceptionRecorder, IAsyncDisposable
{
    public const string ModeId = "drm";
    private const int CreateAttempts = 20;

    private readonly ISstvSessionService _session;
    private readonly IReceivedImageDecoder _imageDecoder;
    private readonly IImageSourceWriter _imageWriter;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IRadioStateProvider _radioState;
    private readonly ILogger<DrmReceptionRecorder> _logger;
    private readonly object _gate = new();
    private Task _chain = Task.CompletedTask;
    private Task<bool>? _drainTask;
    private bool _stopping;
    private int _failedSaves;

    public DrmReceptionRecorder(ISstvSessionService session, IReceivedImageDecoder imageDecoder, IImageSourceWriter imageWriter,
        IReceiveHistoryStore historyStore, ISettingsStore settingsStore, IRadioStateProvider radioState, ILogger<DrmReceptionRecorder> logger)
    {
        _session = session;
        _imageDecoder = imageDecoder;
        _imageWriter = imageWriter;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _radioState = radioState;
        _logger = logger;
        _session.DrmFileReceived += OnFileReceived;
    }

    /// <inheritdoc/>
    public event Action<DrmStoredFile>? FileStored;

    /// <inheritdoc/>
    public Task<bool> DrainAsync(TimeSpan? timeout = null)
    {
        Task chain;
        lock (_gate)
        {
            if (_drainTask is not null)
                return _drainTask;
            _stopping = true;
            chain = _chain;
            _drainTask = DrainCoreAsync(chain, timeout ?? TimeSpan.FromSeconds(30));
        }

        _session.DrmFileReceived -= OnFileReceived;
        return _drainTask;
    }

    public ValueTask DisposeAsync() => new(DrainAsync());

    private void OnFileReceived(DrmReceivedFile file)
    {
        RadioState? radio = _radioState.Current;
        DateTimeOffset receivedAt = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_stopping)
            {
                Log.RefusedAfterDrain(_logger, file.FileName);
                return;
            }

            _chain = _chain.ContinueWith(
                _ => SaveAsync(file, receivedAt, radio), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SaveAsync(DrmReceivedFile file, DateTimeOffset receivedAt, RadioState? radio)
    {
        try
        {
            string drmDirectory = await ReceiveHistorySettings.ResolveDrmFilesDirectoryAsync(_settingsStore).ConfigureAwait(false);
            Directory.CreateDirectory(drmDirectory);
            string rawPath = await WriteRawAsync(drmDirectory, file).ConfigureAwait(false);
            Log.RawStored(_logger, rawPath, file.Data.Length);

            ReceiveHistoryEntry? entry = null;
            try
            {
                entry = await RecordImageAsync(file, receivedAt, radio).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The raw file is already safe; report it as stored, without a history row.
                Interlocked.Increment(ref _failedSaves);
                Log.ImageRecordFailed(_logger, file.FileName, ex);
            }

            RaiseFileStored(new DrmStoredFile(file, rawPath, entry));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedSaves);
            Log.SaveFailed(_logger, file.FileName, ex);
        }
    }

    private async Task<ReceiveHistoryEntry?> RecordImageAsync(DrmReceivedFile file, DateTimeOffset receivedAt, RadioState? radio)
    {
        ReceiveHistoryEntry? entry = null;
        IImageSource? image = file.IsQsstvHybridName ? null : _imageDecoder.TryDecode(file.Data.Span);
        if (image is not null)
        {
            string imagesDirectory = await ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore).ConfigureAwait(false);
            Directory.CreateDirectory(imagesDirectory);
            string entryId = Guid.NewGuid().ToString();
            string pngPath = Path.Combine(imagesDirectory, $"{receivedAt:yyyyMMdd-HHmmssfff}_{ModeId}_{entryId[..8]}.png");
            await _imageWriter.WritePngAsync(image, pngPath).ConfigureAwait(false);

            // ReceptionId 0 keeps RxStationIdAttacher and RxAudioAutoSaver off this row.
            entry = new ReceiveHistoryEntry(
                entryId, receivedAt, ModeId, pngPath, LinkedQsoId: null, DecodeState: ReceiveDecodeState.Completed,
                FrequencyHz: radio?.FrequencyHz, RigMode: radio?.Mode,
                DecodedCallsign: string.IsNullOrEmpty(file.Callsign) ? null : file.Callsign,
                DecodedCallsignSource: string.IsNullOrEmpty(file.Callsign) ? null : StationIdSources.Drm)
            {
                ReceptionId = 0,
            };
            await _historyStore.RecordAsync(entry).ConfigureAwait(false);
            Log.ImageRecorded(_logger, pngPath, file.Callsign);
        }

        return entry;
    }

    // MakeUnique, then CreateNew: a file that appears between the check and the create is never overwritten.
    private static async Task<string> WriteRawAsync(string directory, DrmReceivedFile file)
    {
        for (int attempt = 0; ; attempt++)
        {
            // Sanitized again here: the recorder never trusts a name it did not make safe itself.
            string name = DrmReceivedFileNames.MakeUnique(DrmReceivedFileNames.Sanitize(file.FileName), n => File.Exists(Path.Combine(directory, n)));
            string path = Path.Combine(directory, name);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            }
            catch (IOException) when (attempt < CreateAttempts && File.Exists(path))
            {
                continue;
            }

            try
            {
                await using (stream.ConfigureAwait(false))
                    await stream.WriteAsync(file.Data).ConfigureAwait(false);
                return path;
            }
            catch
            {
                // Our own partial file only: CreateNew guarantees nobody else's.
                TryDelete(path);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RaiseFileStored(DrmStoredFile stored)
    {
        try
        {
            FileStored?.Invoke(stored);
        }
        catch (Exception ex)
        {
            Log.FileStoredHandlerFailed(_logger, ex);
        }
    }

    private async Task<bool> DrainCoreAsync(Task chain, TimeSpan timeout)
    {
        try
        {
            await chain.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.DrainTimedOut(_logger, timeout);
            return false;
        }

        int failures = Volatile.Read(ref _failedSaves);
        Log.DrainCompleted(_logger, failures);
        return failures == 0;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "DRM file stored: {Path} ({Bytes} bytes)")]
        public static partial void RawStored(ILogger logger, string path, int bytes);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM image recorded to history: {Path}, callsign '{Callsign}'")]
        public static partial void ImageRecorded(ILogger logger, string path, string callsign);

        [LoggerMessage(Level = LogLevel.Error, Message = "DRM file {FileName} was stored raw, but its gallery image or history row failed")]
        public static partial void ImageRecordFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Storing DRM file {FileName} failed")]
        public static partial void SaveFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM file {FileName} arrived after the shutdown drain began; not stored")]
        public static partial void RefusedAfterDrain(ILogger logger, string fileName);

        [LoggerMessage(Level = LogLevel.Error, Message = "A DRM FileStored handler threw")]
        public static partial void FileStoredHandlerFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM file drain did not finish within {Timeout}")]
        public static partial void DrainTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM file drain completed, {Failures} failed save(s)")]
        public static partial void DrainCompleted(ILogger logger, int failures);
    }
}
