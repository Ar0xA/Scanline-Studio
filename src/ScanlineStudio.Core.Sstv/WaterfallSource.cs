using System.Reactive.Subjects;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>See <see cref="IWaterfallSource"/>'s own doc comment for the concurrency contract and why
/// this is decode-independent by construction. Hann-windowed, 50%-overlap-by-default short-time FFT;
/// not a legacy port (see <see cref="RadixTwoFft"/>'s own doc comment).</summary>
public sealed class WaterfallSource : IWaterfallSource, IWaterfallSourceReconfiguration, IDisposable
{
    // NOT readonly (restart-required-settings backlog item 4, 2026-08-27) -- volatile, not
    // lock-guarded: PushSamples/BuildFrame (the audio drain thread) reads it once per emitted frame
    // only to compute binWidthHz, and RequestSampleRate (any thread, called by
    // ScanlineStudio.Application.SstvSessionService at the exact moment it commits an RX capture
    // restart -- see IWaterfallSourceReconfiguration's own doc comment for why it must never be
    // called any earlier) writes it -- no other state depends on this value, so a plain volatile
    // int is sufficient and cheaper than a lock on this hot per-frame path.
    private volatile int _sampleRate;
    private readonly int _windowSize;
    private readonly int _hopSize;
    private readonly float[] _hannWindow;
    private readonly float[] _accumulator;
    private readonly Subject<WaterfallFrame> _frames = new();
    private int _accumulatedCount;

    public WaterfallSource(int sampleRate, int windowSize = 1024, int? hopSize = null)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be a power of two.");
        }

        var resolvedHopSize = hopSize ?? windowSize / 2;
        if (resolvedHopSize <= 0 || resolvedHopSize > windowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(hopSize), hopSize, "Hop size must be in (0, windowSize].");
        }

        _sampleRate = sampleRate;
        _windowSize = windowSize;
        _hopSize = resolvedHopSize;
        _hannWindow = BuildHannWindow(windowSize);
        _accumulator = new float[windowSize];
    }

    /// <summary>Sample rate used to map FFT bins to frequencies. Genuinely live now
    /// (restart-required-settings backlog item 4) -- see <see cref="RequestSampleRate"/>.</summary>
    public int SampleRate => _sampleRate;

    public IObservable<WaterfallFrame> Frames => _frames;

    /// <summary>See <see cref="IWaterfallSourceReconfiguration.RequestSampleRate"/>. Also discards
    /// any partially-filled accumulator window (round-4 plan-review nit N2) -- otherwise up to
    /// <c>windowSize - 1</c> samples captured at the OLD rate would get FFT'd together with new-rate
    /// samples and labelled with the NEW bin width. A straggler <see cref="PushSamples"/> call racing
    /// this reset (e.g. on the same abandoned-capture-stop path
    /// <see cref="RestartableSstvDecoder.ApplyPendingReconfigurationNow"/>'s own doc comment
    /// describes) is verified benign, not merely assumed: only ever shrinks the effective fill index
    /// (see <c>Math.Min(_windowSize - _accumulatedCount, ...)</c> in <see cref="PushSamples"/>), so a
    /// racing write cannot go out-of-bounds or throw from the <c>CopyTo</c> call.</summary>
    public void RequestSampleRate(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        _sampleRate = sampleRate;
        _accumulatedCount = 0;
    }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        var span = samples.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            var toCopy = Math.Min(_windowSize - _accumulatedCount, span.Length - offset);
            span.Slice(offset, toCopy).CopyTo(_accumulator.AsSpan(_accumulatedCount));
            _accumulatedCount += toCopy;
            offset += toCopy;

            if (_accumulatedCount == _windowSize)
            {
                // T1-2 (production_audit.md): compute the frame BEFORE sliding the accumulator, and
                // slide BEFORE publishing (_frames.OnNext) -- a throwing subscriber must never leave
                // the accumulator un-slid. It used to: OnNext ran first (inside the old EmitFrame),
                // and a throw from it skipped the Array.Copy/_accumulatedCount reset below entirely,
                // so _accumulatedCount stayed at _windowSize. The VERY NEXT PushSamples call then
                // immediately re-triggered this branch with the SAME stale accumulator content before
                // consuming any of the newly-pushed samples -- a duplicate, stale frame silently
                // re-emitted (confirmed empirically: an all-zeros window's -180dB floor frame,
                // re-published a second time ahead of the genuinely fresh one). Building the frame
                // data and sliding first means the accumulator is always left correct regardless of
                // what a subscriber does with the frame handed to it below.
                var frame = BuildFrame();

                var keep = _windowSize - _hopSize;
                Array.Copy(_accumulator, _hopSize, _accumulator, 0, keep);
                _accumulatedCount = keep;

                _frames.OnNext(frame);
            }
        }
    }

    public void Dispose() => _frames.Dispose();

    // T1-2 (production_audit.md): pure computation only -- no _frames.OnNext here. See this method's
    // own call site in PushSamples for why publishing is deliberately separated from computing.
    // Precondition (unchecked -- single call site, always holds there): _accumulatedCount ==
    // _windowSize, and _accumulator must not yet have been slid for this window -- this reads all
    // _windowSize entries unconditionally, with no awareness of fill state.
    private WaterfallFrame BuildFrame()
    {
        Span<float> real = new float[_windowSize];
        Span<float> imag = new float[_windowSize];
        for (var i = 0; i < _windowSize; i++)
        {
            // ultracode audit finding #18: legacy clamps input to +-32768 before windowing
            // (Fft.cpp:667-675). Citation correction (Tier A Batch 9 chunk 9a): legacy's clamp is a
            // plain `&gt;`/`&lt;` comparison, which is false for NaN -- it clamps +-Inf but lets NaN
            // pass through UNCLAMPED into legacy's own FFT. This port's guard is strictly wider (Inf
            // AND NaN), so the practical outcome (no corrupt frame either way) is still correct; only
            // the "a non-finite raw sample can never reach its FFT" framing overstated what legacy's
            // clamp actually covers. This port has no equivalent guard, and windowing alone doesn't
            // help -- _hannWindow[0] AND _hannWindow[size-1] are both exactly 0f (confirmed both
            // ends, not just index 0), so Inf*0=NaN corrupts the frame regardless of which sample or
            // which window edge was non-finite. Guarded in this port's own [-1,1] domain, not
            // legacy's +-32768 (a display-only class, no legacy sample scale to match here).
            var sample = _accumulator[i];
            real[i] = float.IsFinite(sample) ? sample * _hannWindow[i] : 0f;
        }

        RadixTwoFft.Forward(real, imag);

        var binCount = _windowSize / 2;
        var magnitudesDb = new float[binCount];
        for (var k = 0; k < binCount; k++)
        {
            var magnitude = MathF.Sqrt((real[k] * real[k]) + (imag[k] * imag[k]));
            magnitudesDb[k] = 20f * MathF.Log10(MathF.Max(magnitude, 1e-9f));
        }

        var binWidthHz = (double)_sampleRate / _windowSize;
        return new WaterfallFrame(magnitudesDb, binWidthHz, DateTimeOffset.UtcNow);
    }

    private static float[] BuildHannWindow(int size)
    {
        var window = new float[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }

        return window;
    }
}
