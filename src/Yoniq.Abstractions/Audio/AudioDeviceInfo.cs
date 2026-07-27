namespace Yoniq.Abstractions.Audio;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    int MaxInputChannels,
    int MaxOutputChannels,
    IReadOnlyList<int> SupportedSampleRates);
