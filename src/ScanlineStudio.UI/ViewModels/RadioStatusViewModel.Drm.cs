using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>HamDRM receive status: the status-bar chip and the HamDRM half of <see cref="IsDecodingImage"/>.</summary>
/// <remarks><para><b>Threading.</b> <see cref="ISstvSessionService.DrmSignalStateChanged"/> and
/// <see cref="IDrmReceptionRecorder.FileStored"/> arrive on worker, decode, pool or audio threads. Their handlers only
/// store the value under <see cref="_drmLock"/> (latest wins, a state older than the newest generation is dropped) and
/// return, so a slow UI never backs up the producer. The 250 ms <see cref="OnRxAudioLevelTick"/> reads it on the UI
/// thread.</para>
/// <para><b>Chip precedence:</b> receiving &gt; sync lost &gt; received &gt; sync. "Sync lost" holds
/// <see cref="DrmSyncLostHoldTicks"/> after frame sync drops mid-file; "received" shows for
/// <see cref="DrmReceivedTicks"/> after a file is stored and survives the receiver stopping.</para></remarks>
public sealed partial class RadioStatusViewModel
{
    /// <summary>5 s at the 250 ms poll.</summary>
    public const int DrmSyncLostHoldTicks = 20;

    /// <summary>8 s at the 250 ms poll.</summary>
    public const int DrmReceivedTicks = 32;

    private const int MaxSenderTextLength = 48;

    private readonly object _drmLock = new();
    private DrmSignalState? _drmState;
    private long _drmNewestGeneration = long.MinValue;
    private string? _drmStoredName;

    // UI-thread-only tick state.
    private bool _drmFileInProgress;
    private int _drmSyncLostTicks;
    private int _drmReceivedTicksLeft;
    private string _drmReceivedName = string.Empty;

    // The last frame-synced state; a sync-lost state carries mode and occupancy −1.
    private DrmSignalState? _drmLastSynced;

    /// <summary>The status-bar HamDRM chip text; null hides the chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrmChipVisible))]
    private string? _drmChipText;

    [ObservableProperty]
    private string? _drmChipTooltip;

    public bool IsDrmChipVisible => DrmChipText is not null;

    /// <summary>A HamDRM file is being received now (frame sync, a known file, not yet stored).</summary>
    public bool IsDrmReceiving { get; private set; }

    /// <summary>One 250 ms poll tick, for tests (public: this project has no InternalsVisibleTo).</summary>
    public void PollTickForTests() => OnRxAudioLevelTick();

    private void OnDrmSignalState(DrmSignalState state)
    {
        lock (_drmLock)
        {
            if (state.Generation < _drmNewestGeneration)
                return;
            _drmNewestGeneration = state.Generation;
            _drmState = state;
        }
    }

    private void OnDrmFileStored(DrmStoredFile stored)
    {
        lock (_drmLock)
            _drmStoredName = stored.File.FileName;
    }

    /// <summary>One poll tick: updates the chip and returns <see cref="IsDrmReceiving"/>.</summary>
    private bool UpdateDrmStatus()
    {
        DrmSignalState? st;
        string? storedName;
        lock (_drmLock)
        {
            st = _drmState;
            storedName = _drmStoredName;
            _drmStoredName = null;
        }

        if (storedName is not null)
        {
            _drmReceivedName = Sanitize(storedName);
            _drmReceivedTicksLeft = DrmReceivedTicks;
        }
        else if (_drmReceivedTicksLeft > 0)
        {
            _drmReceivedTicksLeft--;
        }

        if (st is { IsActive: true, FrameSync: true })
            _drmLastSynced = st;
        else if (st is not { IsActive: true })
            _drmLastSynced = null;

        bool receiving = st is { IsActive: true, FrameSync: true, CurrentFileComplete: false }
            && (st.ExpectedSegments > 0 || st.TotalSegments > 0);
        if (receiving)
        {
            _drmFileInProgress = true;
            _drmSyncLostTicks = 0;
        }
        else if (st is { IsActive: true, FrameSync: false, CurrentFileComplete: false } && _drmFileInProgress
                 && _drmSyncLostTicks < DrmSyncLostHoldTicks)
        {
            _drmSyncLostTicks++;
        }
        else
        {
            _drmFileInProgress = false;
            _drmSyncLostTicks = 0;
        }

        bool syncLost = !receiving && _drmFileInProgress;
        IsDrmReceiving = receiving;
        DrmChipText = receiving ? ReceivingText(st!)
            : syncLost ? _localization.GetString("RadioStatus.Drm.SyncLost")
            : _drmReceivedTicksLeft > 0 ? _localization.GetString("RadioStatus.Drm.Received", _drmReceivedName)
            : st is { IsActive: true, FrameSync: true } ? Join(Prefix(st), _localization.GetString("RadioStatus.Drm.Sync"))
            : null;
        DrmSignalState? tip = receiving ? st : syncLost ? _drmLastSynced : st is { IsActive: true, FrameSync: true } ? st : null;
        DrmChipTooltip = DrmChipText is null || tip is null ? null
            : _localization.GetString(
                "RadioStatus.Drm.Tooltip",
                tip.ReceivingName.Length > 0 ? Sanitize(tip.ReceivingName) : _localization.GetString("RadioStatus.Drm.NoName"),
                ModeLetter(tip.RobustnessMode),
                _localization.GetString(tip.SpectrumOccupancy == 0 ? "RadioStatus.Drm.Occupancy0" : "RadioStatus.Drm.Occupancy1"));
        return receiving;
    }

    private string ReceivingText(DrmSignalState st)
    {
        string progress = st.ExpectedSegments > 0
            ? _localization.GetString("RadioStatus.Drm.Progress", st.SegmentsReceived, st.ExpectedSegments)
            : _localization.GetString("RadioStatus.Drm.Segments", st.SegmentsReceived);
        string callsign = Sanitize(st.Callsign);
        return callsign.Length > 0 ? Join(Prefix(st), callsign, progress) : Join(Prefix(st), progress);
    }

    private string Prefix(DrmSignalState st) => _localization.GetString("RadioStatus.Drm.Prefix", ModeLetter(st.RobustnessMode));

    private static string Join(params string[] parts) => string.Join(" · ", parts);

    public static string ModeLetter(int robustnessMode) => robustnessMode switch
    {
        0 => "A",
        1 => "B",
        2 => "E",
        _ => "?",
    };

    /// <summary>Sender-chosen text: control characters removed, trimmed, length capped.</summary>
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxSenderTextLength));
        foreach (char c in text)
        {
            if (!char.IsControl(c))
                sb.Append(c);
            if (sb.Length == MaxSenderTextLength)
                break;
        }

        return sb.ToString().Trim();
    }
}
