using System.Runtime.InteropServices;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// Scriptable <see cref="IHamlibNative"/> test double -- the "fake native-call shim"
/// spec/03-cat-layer.md's Testing section promises. Tracks <see cref="CallLog"/> (in order) for
/// connect-sequence/dispose-ordering assertions, and <see cref="Reentered"/> to prove
/// <see cref="HamlibRadioProtocol"/>'s <c>SemaphoreSlim</c> actually serializes concurrent
/// <c>PollAsync</c>/<c>Set*Async</c> calls against it -- every call goes through
/// <see cref="Enter{T}"/>, which records whether a second call ever entered while a first one was
/// still inside (optionally slowed down via <see cref="CallDelay"/> to give a concurrency test a
/// window to attempt exactly that).
/// </summary>
internal sealed class FakeHamlibNative : IHamlibNative
{
    private int _callDepth;

    public List<string> CallLog { get; } = [];
    public bool Reentered { get; private set; }
    public TimeSpan CallDelay { get; set; } = TimeSpan.Zero;

    public nint RigInitHandle { get; set; } = 1;
    public int RigOpenCode { get; set; }
    public int RigCloseCode { get; set; }
    public int RigCleanupCode { get; set; }

    public Dictionary<string, CLong> Tokens { get; } = new(StringComparer.Ordinal)
    {
        ["rig_pathname"] = new CLong(1),
        ["serial_speed"] = new CLong(2),
        ["ptt_type"] = new CLong(3),
    };

    public int RigSetConfCode { get; set; }
    public int RigSetFreqCode { get; set; }
    public int RigGetFreqCode { get; set; }
    public double Frequency { get; set; } = 14_074_000;
    public int RigSetModeCode { get; set; }
    public int RigGetModeCode { get; set; }
    public ulong Mode { get; set; } = 1UL << 2; // RIG_MODE_USB
    public int RigSetPttCode { get; set; }
    public int RigGetPttCode { get; set; }
    public int Ptt { get; set; }
    public string? Version { get; set; } = "Hamlib 4.5.5 2024-01-01T00:00:00Z 64-bit";

    public Dictionary<ulong, int> LevelCodes { get; } = new();
    public Dictionary<ulong, float> LevelValues { get; } = new();
    public Dictionary<ulong, int> LevelIntValues { get; } = new();

    public int RigLoadAllBackendsCode { get; set; }
    public List<uint> ModelIds { get; } = [];
    public Dictionary<uint, string> CapsMfgNames { get; } = new();
    public Dictionary<uint, string> CapsModelNames { get; } = new();

    public nint RigInit(uint model) => Enter(() =>
    {
        CallLog.Add("rig_init");
        return RigInitHandle;
    });

    public int RigOpen(nint rig) => Enter(() =>
    {
        CallLog.Add("rig_open");
        return RigOpenCode;
    });

    public int RigClose(nint rig) => Enter(() =>
    {
        CallLog.Add("rig_close");
        return RigCloseCode;
    });

    public int RigCleanup(nint rig) => Enter(() =>
    {
        CallLog.Add("rig_cleanup");
        return RigCleanupCode;
    });

    public CLong RigTokenLookup(nint rig, string name) => Enter(() =>
    {
        CallLog.Add($"rig_token_lookup:{name}");
        return Tokens.GetValueOrDefault(name, default);
    });

    public int RigSetConf(nint rig, CLong token, string value) => Enter(() =>
    {
        CallLog.Add($"rig_set_conf:{value}");
        return RigSetConfCode;
    });

    public int RigSetFreq(nint rig, uint vfo, double freq) => Enter(() =>
    {
        CallLog.Add("rig_set_freq");
        Frequency = freq;
        return RigSetFreqCode;
    });

    public int RigGetFreq(nint rig, uint vfo, out double freq)
    {
        var code = Enter(() =>
        {
            CallLog.Add("rig_get_freq");
            return RigGetFreqCode;
        });
        freq = Frequency;
        return code;
    }

    public int RigSetMode(nint rig, uint vfo, ulong mode, CLong width) => Enter(() =>
    {
        CallLog.Add("rig_set_mode");
        Mode = mode;
        return RigSetModeCode;
    });

    public int RigGetMode(nint rig, uint vfo, out ulong mode, out CLong width)
    {
        var code = Enter(() =>
        {
            CallLog.Add("rig_get_mode");
            return RigGetModeCode;
        });
        mode = Mode;
        width = default;
        return code;
    }

    public int RigSetPtt(nint rig, uint vfo, int ptt) => Enter(() =>
    {
        CallLog.Add("rig_set_ptt");
        Ptt = ptt;
        return RigSetPttCode;
    });

    public int RigGetPtt(nint rig, uint vfo, out int ptt)
    {
        var code = Enter(() =>
        {
            CallLog.Add("rig_get_ptt");
            return RigGetPttCode;
        });
        ptt = Ptt;
        return code;
    }

    public string? RigVersion() => Enter(() =>
    {
        CallLog.Add("rig_version");
        return Version;
    });

    public int RigGetLevel(nint rig, uint vfo, ulong level, out float value)
    {
        var code = Enter(() =>
        {
            CallLog.Add($"rig_get_level:{level}");
            return LevelCodes.GetValueOrDefault(level, 0);
        });
        value = LevelValues.GetValueOrDefault(level, 0f);
        return code;
    }

    public int RigGetLevelInt(nint rig, uint vfo, ulong level, out int value)
    {
        var code = Enter(() =>
        {
            CallLog.Add($"rig_get_level:{level}");
            return LevelCodes.GetValueOrDefault(level, 0);
        });
        value = LevelIntValues.GetValueOrDefault(level, 0);
        return code;
    }

    public int RigLoadAllBackends() => Enter(() =>
    {
        CallLog.Add("rig_load_all_backends");
        return RigLoadAllBackendsCode;
    });

    public IReadOnlyList<uint> RigListModelIds() => Enter(() =>
    {
        CallLog.Add("rig_list_foreach_model");
        return (IReadOnlyList<uint>)ModelIds;
    });

    public string? RigGetCapsMfgName(uint model) => Enter(() =>
    {
        CallLog.Add($"rig_get_caps_cptr:mfg:{model}");
        return CapsMfgNames.GetValueOrDefault(model);
    });

    public string? RigGetCapsModelName(uint model) => Enter(() =>
    {
        CallLog.Add($"rig_get_caps_cptr:model:{model}");
        return CapsModelNames.GetValueOrDefault(model);
    });

    private T Enter<T>(Func<T> body)
    {
        if (Interlocked.Increment(ref _callDepth) > 1)
        {
            Reentered = true;
        }

        try
        {
            if (CallDelay > TimeSpan.Zero)
            {
                Thread.Sleep(CallDelay);
            }

            return body();
        }
        finally
        {
            Interlocked.Decrement(ref _callDepth);
        }
    }
}
