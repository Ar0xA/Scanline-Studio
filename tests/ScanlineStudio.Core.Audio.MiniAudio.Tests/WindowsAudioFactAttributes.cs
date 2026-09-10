namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Gating for the Windows-only audio tests (`BACKLOG.md` W2-W4), split by what each one COSTS the
/// machine it runs on. The tier is part of the attribute name so a reader can see a test's device
/// impact at its declaration rather than having to read its body.
///
/// <list type="bullet">
/// <item><see cref="WindowsFactAttribute"/> — touches no audio device at all.</item>
/// <item><see cref="WindowsAudioReadOnlyFactAttribute"/> — enumerates devices or reads their state,
/// never opens a stream. Inaudible, interrupts nothing, safe to run while music is playing.</item>
/// <item><see cref="WindowsAudioExclusiveFactAttribute"/> — opens a device or changes system state.
/// Opt-in only, never runs by default.</item>
/// </list>
///
/// <para><b>Why the third tier is opt-in rather than merely slow.</b> Opening a capture or loopback
/// endpoint can interrupt whatever else is using it, and a mute-state oracle has to change the
/// machine's actual mute state to have anything to assert against. Neither belongs in a run someone
/// kicked off while doing something else. They are gated behind an environment variable so the
/// decision to pay that cost is always explicit.</para>
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: this asserts behaviour of a Windows platform API.";
        }
    }
}

/// <summary>
/// Windows-only, and reads device state WITHOUT opening a stream. Enumeration and
/// <c>IAudioEndpointVolume</c> queries both fall here: they go through the device collection and the
/// endpoint's property store, so nothing becomes audible and no other application loses its device.
/// Safe to run at any time.
/// </summary>
public sealed class WindowsAudioReadOnlyFactAttribute : FactAttribute
{
    public WindowsAudioReadOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: this reads WASAPI device state through a Windows API.";
        }
    }
}

/// <summary>
/// Windows-only AND takes real exclusive cost — opens an audio endpoint, or changes a system-wide
/// setting such as mute and restores it afterwards. Set <c>SCANLINE_WINDOWS_AUDIO_EXCLUSIVE=1</c> to
/// run these.
///
/// <para>Every test wearing this attribute must state in its own doc comment exactly what it takes
/// and how it restores it. That is not a convention — it is the whole reason the tier exists.</para>
/// </summary>
public sealed class WindowsAudioExclusiveFactAttribute : FactAttribute
{
    public const string OptInVariable = "SCANLINE_WINDOWS_AUDIO_EXCLUSIVE";

    public WindowsAudioExclusiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: this exercises a Windows audio endpoint.";
            return;
        }

        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Opt-in: set {OptInVariable}=1. This test opens an audio device or changes the "
                + "machine's mute state, so it can interrupt whatever else is using audio.";
        }
    }
}
