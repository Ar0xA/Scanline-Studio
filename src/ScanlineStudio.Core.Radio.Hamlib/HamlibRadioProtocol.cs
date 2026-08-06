using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md. <see cref="IRadioProtocol"/> for Hamlib linked in-process via
/// <see cref="IHamlibNative"/> (constructor-injected as the interface, not the concrete
/// <see cref="HamlibNative"/>, so tests can substitute a fake).
///
/// Lazily <c>rig_init</c>/config/<c>rig_open</c>s and probes capabilities on the first call to any
/// public method, not in the constructor (which can't be async) -- mirrors
/// <c>RigctldClientProtocol.EnsureConnectedAsync</c>'s shape.
///
/// <b>Internal serialization + offload</b>: Hamlib's C API is not thread-safe per <c>RIG *</c> handle,
/// and every <c>rig_*</c> call blocks on serial I/O for up to hundreds of milliseconds
/// (spec/03-cat-layer.md's Threading contract). A <see cref="SemaphoreSlim"/> serializes every native
/// call (mutual exclusion, mirroring <c>RigctldClientProtocol</c>'s own <c>_requestLock</c>); each call
/// is additionally wrapped in <see cref="Task.Run(Action)"/> so it never runs inline on the caller's
/// thread (offload -- the UI thread, for a PTT keystroke, would otherwise freeze for the duration of
/// the call). These are separate concerns, both needed. <paramref name="ct"/> (on the public methods)
/// is honored only at the semaphore's own <c>WaitAsync</c> boundary -- once a native call starts, it
/// is never cancelled or abandoned, since an in-flight, uncancellable native call sharing the same
/// <c>RIG *</c> with a "cancelled" caller's replacement would race it. Accepted tradeoff: with a
/// (approximately) FIFO semaphore, <see cref="SetPttAsync"/> (the SSTV TX-keying hot path) can queue
/// behind an in-flight poll -- the real bound is the duration of whatever's currently in the critical
/// section (up to 3 blocking native calls while receiving, up to 6 while transmitting once the
/// SWR/ALC/RFPOWER_METER meter reads are gated in -- see <see cref="PollAsync"/> -- or a full
/// connect+probe sequence on the very first poll), not just the poll cadence.
/// </summary>
public sealed partial class HamlibRadioProtocol : IRadioProtocol
{
    // RIG_MODE_* bit-flag values (hamlib/include/hamlib/rig.h, CONSTANT_64BIT_FLAG(n) = 1UL << n) --
    // exact-value equality only: rig_get_mode always returns exactly one flag despite the bitmask
    // type, never decompose bits. Reuses RigctldClientProtocol's own documented
    // PKTUSB=Data/PKTLSB=DataR/PKTFM=Pkt convention rather than re-deciding it.
    private const ulong ModeAm = 1UL << 0;
    private const ulong ModeCw = 1UL << 1;
    private const ulong ModeUsb = 1UL << 2;
    private const ulong ModeLsb = 1UL << 3;
    private const ulong ModeRtty = 1UL << 4;
    private const ulong ModeFm = 1UL << 5;
    private const ulong ModeCwR = 1UL << 7;
    private const ulong ModeRttyR = 1UL << 8;
    private const ulong ModePktLsb = 1UL << 10;
    private const ulong ModePktUsb = 1UL << 11;
    private const ulong ModePktFm = 1UL << 12;

    private static readonly Dictionary<ulong, RadioMode> HamlibToMode = new()
    {
        [ModeAm] = RadioMode.Am,
        [ModeCw] = RadioMode.Cw,
        [ModeUsb] = RadioMode.Usb,
        [ModeLsb] = RadioMode.Lsb,
        [ModeRtty] = RadioMode.Rtty,
        [ModeFm] = RadioMode.Fm,
        [ModeCwR] = RadioMode.CwR,
        [ModeRttyR] = RadioMode.RttyR,
        [ModePktLsb] = RadioMode.DataR,
        [ModePktUsb] = RadioMode.Data,
        [ModePktFm] = RadioMode.Pkt,
    };

    private static readonly Dictionary<RadioMode, ulong> ModeToHamlib = new()
    {
        [RadioMode.Am] = ModeAm,
        [RadioMode.Cw] = ModeCw,
        [RadioMode.Usb] = ModeUsb,
        [RadioMode.Lsb] = ModeLsb,
        [RadioMode.Rtty] = ModeRtty,
        [RadioMode.Fm] = ModeFm,
        [RadioMode.CwR] = ModeCwR,
        [RadioMode.RttyR] = ModeRttyR,
        [RadioMode.DataR] = ModePktLsb,
        [RadioMode.Data] = ModePktUsb,
        [RadioMode.Pkt] = ModePktFm,
    };

    // Hamlib error codes are negative; this is RIG_IS_SOFT_ERRCODE's exact member list (rig.h) --
    // command-level (RadioProtocolException, RadioController doesn't back off) vs. transport-level
    // (plain exception, triggers RadioController's existing dispose+backoff+reconnect).
    private static readonly HashSet<int> SoftErrorCodes =
    [
        1,  // RIG_EINVAL
        4,  // RIG_ENIMPL
        9,  // RIG_ERJCTED
        10, // RIG_ETRUNC
        11, // RIG_ENAVAIL
        12, // RIG_ENTARGET
        16, // RIG_EVFO
        17, // RIG_EDOM
        18, // RIG_EDEPRECATED
        19, // RIG_ESECURITY
        20, // RIG_EPOWER
    ];

    // Known Hamlib ptt_type config tokens (hamlib/src/conf.c) -- validated client-side before ever
    // calling rig_set_conf. An unrecognized token maps to a *soft* RIG_EINVAL, which without this
    // check would retry as CommandFailed forever instead of surfacing as a clear connect-time failure.
    private static readonly HashSet<string> KnownPttTypes = new(StringComparer.Ordinal)
    {
        "RIG", "RIGMICDATA", "DTR", "RTS", "Parallel", "CM108", "GPIO", "GPION", "None",
    };

    private const uint VfoCurrent = 0x20000000; // RIG_VFO_CURR
    private const int PassbandNormal = 0;       // RIG_PASSBAND_NORMAL
    private const int PttOn = 1;                // RIG_PTT_ON
    private const int PttOff = 0;               // RIG_PTT_OFF

    // RIG_LEVEL_* bit-flag values (hamlib/include/hamlib/rig.h, CONSTANT_64BIT_FLAG(n) = 1ull << n) --
    // verified directly against rig.h, not assumed. setting_t (the rig_get_level level parameter's
    // type) is `typedef uint64_t setting_t` -- a plain ulong, unlike pbwidth_t/hamlib_token_t (which
    // are CLong-marshaled C `long`s elsewhere in this file/IHamlibNative).
    private const ulong LevelSwr = 1UL << 28;
    private const ulong LevelAlc = 1UL << 29;
    private const ulong LevelRfPowerMeter = 1UL << 32;

    private readonly IHamlibNative _native;
    private readonly uint _model;
    private readonly string? _serialPort;
    private readonly int? _baudRate;
    private readonly string? _pttType;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private nint _rig;
    private bool _connected;
    private bool _disposed;

    // internal, not public: IHamlibNative is internal (spec/03's IHamlibNative seam -- never exposed
    // outside this assembly). HamlibProtocolFactory (public, same assembly) is the only intended way
    // to obtain an instance from outside; tests construct this directly via InternalsVisibleTo.
    internal HamlibRadioProtocol(
        IHamlibNative native, uint model, string? serialPort = null, int? baudRate = null, string? pttType = null, ILogger? logger = null)
    {
        if (pttType is not null && !KnownPttTypes.Contains(pttType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pttType), pttType, "Not a recognized Hamlib ptt_type config token.");
        }

        _native = native;
        _model = model;
        _serialPort = serialPort;
        _baudRate = baudRate;
        _pttType = pttType;
        _logger = logger ?? NullLogger.Instance;
    }

    public string RigId => "hamlib-native";

    public RadioCapabilities Capabilities { get; private set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync().ConfigureAwait(false);

            var hz = 0L;
            if (Capabilities.HasFlag(RadioCapabilities.ReadFrequency))
            {
                var freq = await CallAsync(() =>
                {
                    ThrowIfError(_native.RigGetFreq(_rig, VfoCurrent, out var value));
                    return value;
                }).ConfigureAwait(false);
                hz = (long)freq;
            }

            var mode = RadioMode.Unknown;
            if (Capabilities.HasFlag(RadioCapabilities.ReadMode))
            {
                mode = await CallAsync(() =>
                {
                    ThrowIfError(_native.RigGetMode(_rig, VfoCurrent, out var value, out _));
                    return HamlibToMode.GetValueOrDefault(value, RadioMode.Unknown);
                }).ConfigureAwait(false);
            }

            var isTransmitting = false;
            if (Capabilities.HasFlag(RadioCapabilities.PttControl))
            {
                isTransmitting = await CallAsync(() =>
                {
                    ThrowIfError(_native.RigGetPtt(_rig, VfoCurrent, out var value));
                    return value != PttOff;
                }).ConfigureAwait(false);
            }

            // Meters are TX-only readings on a real rig -- gated on the PTT readback just obtained
            // above (not a separate always-on probe), both because an RX-time read is meaningless
            // and to avoid doubling this poll's native-call count (already documented above as the
            // real latency bound on this hot path) for a reading nobody looks at outside an active
            // transmit.
            float? swr = null, alc = null, powerPercent = null;
            if (isTransmitting)
            {
                if (Capabilities.HasFlag(RadioCapabilities.SwrMeter))
                {
                    swr = await TryReadMeterAsync(LevelSwr).ConfigureAwait(false);
                }

                if (Capabilities.HasFlag(RadioCapabilities.AlcMeter))
                {
                    alc = await TryReadMeterAsync(LevelAlc).ConfigureAwait(false);
                }

                if (Capabilities.HasFlag(RadioCapabilities.PowerMeter))
                {
                    var fraction = await TryReadMeterAsync(LevelRfPowerMeter).ConfigureAwait(false);
                    powerPercent = fraction * 100f;
                }
            }

            return new RadioState(hz, mode, isTransmitting, SignalStrengthDb: null, DateTimeOffset.UtcNow, swr, alc, powerPercent);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetFrequencyAsync(long hz, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            await CallAsync(() => ThrowIfError(_native.RigSetFreq(_rig, VfoCurrent, hz))).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetModeAsync(RadioMode mode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ModeToHamlib.TryGetValue(mode, out var hamlibMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "This RadioMode has no Hamlib RIG_MODE_* equivalent.");
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            await CallAsync(() =>
                ThrowIfError(_native.RigSetMode(_rig, VfoCurrent, hamlibMode, new CLong(PassbandNormal))))
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetPttAsync(bool tx, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            await CallAsync(() => ThrowIfError(_native.RigSetPtt(_rig, VfoCurrent, tx ? PttOn : PttOff)))
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Must only be called while holding <see cref="_lock"/>. Connect sequence:
    /// <c>rig_init</c> -&gt; <c>rig_token_lookup</c>+<c>rig_set_conf</c> for pathname/speed/ptt_type
    /// (only non-null fields) -&gt; <c>rig_open</c>. A failure anywhere after a successful
    /// <c>rig_init</c> still frees the handle via <c>rig_cleanup</c> before propagating -- exactly one
    /// cleanup call site, so a failed <c>rig_open</c> (which itself needs cleanup) can never be
    /// double-freed by also going through this catch.</summary>
    private async Task EnsureConnectedAsync()
    {
        if (_connected)
        {
            return;
        }

        await CallAsync(() =>
        {
            _rig = _native.RigInit(_model);
            if (_rig == nint.Zero)
            {
                throw new IOException($"rig_init failed for Hamlib model {_model} (returned a null handle).");
            }

            try
            {
                ApplyConf("rig_pathname", _serialPort);
                ApplyConf("serial_speed", _baudRate?.ToString(CultureInfo.InvariantCulture));
                ApplyConf("ptt_type", _pttType);

                ThrowIfError(_native.RigOpen(_rig));
            }
            catch (Exception ex)
            {
                // rig_open (or one of the rig_set_conf calls before it) failed -- clean up the handle
                // rig_init allocated and rethrow. The real failure is logged by RadioController, which
                // owns the retry/backoff decision; this just attributes it to this connect step.
                Log.ConnectStepFailed(_logger, _model, ex);
                _native.RigCleanup(_rig);
                _rig = nint.Zero;
                throw;
            }
        }).ConfigureAwait(false);

        Capabilities = await CallAsync(ProbeCapabilities).ConfigureAwait(false);
        _connected = true;
        Log.Connected(_logger, _model, Capabilities);
    }

    private void ApplyConf(string tokenName, string? value)
    {
        if (value is null)
        {
            return;
        }

        var token = _native.RigTokenLookup(_rig, tokenName);
        if (token.Value == 0) // RIG_CONF_END -- not a recognized token, never feed into rig_set_conf
        {
            throw new IOException($"Hamlib does not recognize the config token '{tokenName}'.");
        }

        ThrowIfError(_native.RigSetConf(_rig, token, value));
    }

    /// <summary>Probes <c>rig_get_freq</c>/<c>rig_get_mode</c>/<c>rig_get_ptt</c> once after open,
    /// mapping a *soft* error (function not implemented/available) to "capability absent" -- mirrors
    /// <c>RigctldClientProtocol.ProbeCapabilitiesAsync</c>'s f/m/t probing, since no struct-free
    /// <c>rig_get_caps_*</c> covers per-function get/set flags (spec/03-cat-layer.md). A *hard* error
    /// during probing is a real connect-time failure, not "capability absent" -- surfaced, not
    /// swallowed.</summary>
    private RadioCapabilities ProbeCapabilities()
    {
        var caps = RadioCapabilities.None;

        if (TryProbe(() => _native.RigGetFreq(_rig, VfoCurrent, out _)))
        {
            caps |= RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency;
        }

        if (TryProbe(() => _native.RigGetMode(_rig, VfoCurrent, out _, out _)))
        {
            caps |= RadioCapabilities.ReadMode | RadioCapabilities.SetMode;
        }

        if (TryProbe(() => _native.RigGetPtt(_rig, VfoCurrent, out _)))
        {
            caps |= RadioCapabilities.PttControl;
        }

        if (TryProbe(() => _native.RigGetLevel(_rig, VfoCurrent, LevelSwr, out _)))
        {
            caps |= RadioCapabilities.SwrMeter;
        }

        if (TryProbe(() => _native.RigGetLevel(_rig, VfoCurrent, LevelAlc, out _)))
        {
            caps |= RadioCapabilities.AlcMeter;
        }

        if (TryProbe(() => _native.RigGetLevel(_rig, VfoCurrent, LevelRfPowerMeter, out _)))
        {
            caps |= RadioCapabilities.PowerMeter;
        }

        return caps;
    }

    /// <summary>Reads one meter level -- unlike the freq/mode/ptt reads above (which always
    /// <see cref="ThrowIfError"/> unconditionally), a *soft* error here yields <see langword="null"/>
    /// instead of throwing: meters are far more likely than freq/mode/ptt to intermittently soft-error
    /// (e.g. ENAVAIL for SWR while not actually keyed), and letting that abort the whole
    /// <see cref="PollAsync"/> snapshot would freeze the entire frequency/mode strip for as long as
    /// the condition lasts. A *hard* error still surfaces (via <see cref="ThrowIfError"/>) -- that
    /// indicates a genuinely broken transport, the same as every other read in this method.</summary>
    private async Task<float?> TryReadMeterAsync(ulong level)
    {
        return await CallAsync(() =>
        {
            var code = _native.RigGetLevel(_rig, VfoCurrent, level, out var value);
            if (code == 0)
            {
                return (float?)value;
            }

            if (IsSoftError(code))
            {
                return null;
            }

            ThrowIfError(code); // hard error -- surface it, don't silently return null
            return null;        // unreachable -- ThrowIfError always throws for a nonzero hard code
        }).ConfigureAwait(false);
    }

    private static bool TryProbe(Func<int> call)
    {
        var code = call();
        if (code == 0)
        {
            return true;
        }

        if (IsSoftError(code))
        {
            return false;
        }

        ThrowIfError(code); // hard error at connect time -- surface it, don't silently mark absent
        return false;       // unreachable -- ThrowIfError always throws for a nonzero code
    }

    private static Task<T> CallAsync<T>(Func<T> call) => Task.Run(call);

    private static Task CallAsync(Action call) => Task.Run(call);

    private static void ThrowIfError(int code)
    {
        if (code == 0)
        {
            return;
        }

        if (IsSoftError(code))
        {
            throw new RadioProtocolException($"Hamlib command failed with code {code}.");
        }

        throw new IOException($"Hamlib command failed with transport-level code {code}.");
    }

    private static bool IsSoftError(int code) => SoftErrorCodes.Contains(-code);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                var rig = _rig;
                _rig = nint.Zero;
                _connected = false;
                await Task.Run(() =>
                {
                    _native.RigClose(rig);
                    _native.RigCleanup(rig);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Hamlib connect step failed for model {Model}")]
        public static partial void ConnectStepFailed(ILogger logger, uint model, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Hamlib rig connected: model={Model}, capabilities={Capabilities}")]
        public static partial void Connected(ILogger logger, uint model, RadioCapabilities capabilities);
    }
}
