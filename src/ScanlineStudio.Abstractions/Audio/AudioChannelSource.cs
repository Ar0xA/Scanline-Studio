namespace ScanlineStudio.Abstractions.Audio;

/// <summary>Which channel of a stereo-capable capture device to use as the mono RX source --
/// stereo-capture-source backlog item. <b>Not a confirmed legacy port</b> (implemented as "extract
/// the selected channel from a real stereo device open," a reasonable interpretation, not something
/// traced against actual legacy YONIQ source). <see cref="Mono"/> (value <c>0</c>, matching the CLR
/// default) reproduces the exact pre-existing behavior: the device opens with 1 channel and
/// miniaudio's own data converter handles whatever downmix its real native format needs, unchanged.
/// <see cref="Left"/>/<see cref="Right"/> open the device with 2 channels instead and take only the
/// named channel -- see <c>native/scanline_audio.c</c>'s <c>capture_session_data_callback</c> for the
/// exact extraction. <see cref="IAudioEngine.SamplesCaptured"/> is unaffected either way -- always
/// mono, per that event's own documented contract.</summary>
public enum AudioChannelSource
{
    Mono = 0,
    Left = 1,
    Right = 2,
}
