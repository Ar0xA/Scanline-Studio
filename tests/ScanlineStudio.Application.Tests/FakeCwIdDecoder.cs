using ScanlineStudio.Abstractions.Cw;

namespace ScanlineStudio.Application.Tests;

/// <summary>fsk_cwid.md B-P2: a settable, deterministic <see cref="ICwIdDecoder"/> double -- tests
/// exercising <c>SstvSessionService.CwId.cs</c>'s own arm/capture/hand-off state machine need
/// control over WHAT decodes to (including "nothing," the default), not the real
/// <c>ClassicalCwDecoder</c>'s DSP behavior, which is already covered by its own
/// <c>Core.Cw.Tests</c> suite.</summary>
internal sealed class FakeCwIdDecoder : ICwIdDecoder
{
    public CwDecodeResult Result { get; set; } = new(string.Empty, 0, null, null, []);

    public Exception? ThrowOnDecode { get; set; }

    public int DecodeCallCount { get; private set; }

    /// <summary>Captured from the most recent <see cref="DecodeAsync"/> call -- lets a test assert
    /// exactly which samples the arm handed off (length/sample rate), the same way
    /// <c>FakeSstvEncoder</c>'s own captured-argument properties work.</summary>
    public ReadOnlyMemory<float> LastSamples { get; private set; }

    public int LastSampleRate { get; private set; }

    private readonly TaskCompletionSource _firstCallCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once <see cref="DecodeAsync"/> has been called at least once (regardless of
    /// whether it then threw) -- lets a test await a deterministic signal instead of a fixed
    /// <c>Task.Delay</c> when it needs to observe the call happened but has no <c>CwIdDecoded</c>
    /// event to wait on (e.g. a <see cref="ThrowOnDecode"/> case, which never raises one).</summary>
    public Task FirstCallCompletion => _firstCallCompletion.Task;

    public Task<CwDecodeResult> DecodeAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken ct = default)
    {
        DecodeCallCount++;
        LastSamples = samples;
        LastSampleRate = sampleRate;
        _firstCallCompletion.TrySetResult();

        if (ThrowOnDecode is not null)
        {
            throw ThrowOnDecode;
        }

        return Task.FromResult(Result);
    }
}
