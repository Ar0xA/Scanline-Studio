using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Test-only oracle for <see cref="WasapiMuteQueryTests"/>: sets a Windows endpoint's mute state
/// through COM, so the production query has something INDEPENDENT to be checked against.
///
/// <para><b>Why this exists as its own path rather than reusing the shim.</b> An oracle that went
/// through the same native code as the thing under test would be self-confirming — the exact failure
/// `CLAUDE.md` §4 records from the Scottie incident. This talks to <c>IMMDeviceEnumerator</c> and
/// <c>IAudioEndpointVolume</c> directly, so a bug in the shim's COM handling cannot hide in both
/// halves at once. It is the Windows counterpart of the Linux test's <c>pactl set-sink-mute</c>.</para>
///
/// <para><b>COM here is deliberate and permitted.</b> `CLAUDE.md` §4 bars Win32 and COM interop from
/// <c>ScanlineStudio.Core.*</c> and <c>ScanlineStudio.UI</c>. This is a test project, and the rule's
/// purpose — keeping cross-platform production code free of Windows dependencies — is not touched by
/// a test-only oracle that is itself gated to Windows.</para>
///
/// <para><b>Callers are responsible for restoring state.</b> This type deliberately offers no
/// save-and-restore convenience: every caller reads the original value first and restores it in a
/// <c>finally</c>, so the restoration is visible at the call site rather than hidden here.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsEndpointVolume
{
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>Sets the endpoint's mute flag. <paramref name="deviceId"/> is the WASAPI endpoint id
    /// as this project's enumerator reports it.</summary>
    public static void SetMute(string deviceId, bool mute)
    {
        var enumeratorType = Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)
            ?? throw new InvalidOperationException("MMDeviceEnumerator is not registered on this machine.");

        var enumerator = (IMMDeviceEnumerator)(Activator.CreateInstance(enumeratorType)
            ?? throw new InvalidOperationException("Could not create MMDeviceEnumerator."));

        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out var device));
            try
            {
                var iid = AudioEndpointVolumeIid;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volumeObject));

                var volume = (IAudioEndpointVolume)volumeObject;
                try
                {
                    // A null event context means "no originating event GUID", which is correct for a
                    // caller that is not itself publishing volume notifications.
                    var noEventContext = Guid.Empty;
                    Marshal.ThrowExceptionForHR(volume.SetMute(mute, ref noEventContext));
                }
                finally
                {
                    Marshal.ReleaseComObject(volume);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private const int ClsCtxAll = 0x17;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // Only the vtable slots up to the one used are declared, in order. EnumAudioEndpoints must be
        // present even though it is unused, or GetDevice would resolve to the wrong slot.
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(
            ref Guid iid,
            int clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        // Same rule as above: every preceding vtable slot is declared so SetMute lands on slot 6.
        int RegisterControlChangeNotify(IntPtr notify);

        int UnregisterControlChangeNotify(IntPtr notify);

        int GetChannelCount(out uint count);

        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        int GetMasterVolumeLevel(out float levelDb);

        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        int GetChannelVolumeLevel(uint channel, out float levelDb);

        int GetChannelVolumeLevelScalar(uint channel, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
