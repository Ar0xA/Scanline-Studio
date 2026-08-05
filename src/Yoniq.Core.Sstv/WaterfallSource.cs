using System.Reactive.Subjects;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>See <see cref="IWaterfallSource"/>'s own doc comment for the concurrency contract and why
/// this is decode-independent by construction. Hann-windowed, 50%-overlap-by-default short-time FFT;
/// not a legacy port (see <see cref="RadixTwoFft"/>'s own doc comment).</summary>
public sealed class WaterfallSource : IWaterfallSource, IDisposable
{
    private readonly int _sampleRate;
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

    public IObservable<WaterfallFrame> Frames => _frames;

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
                EmitFrame();

                var keep = _windowSize - _hopSize;
                Array.Copy(_accumulator, _hopSize, _accumulator, 0, keep);
                _accumulatedCount = keep;
            }
        }
    }

    public void Dispose() => _frames.Dispose();

    private void EmitFrame()
    {
        Span<float> real = new float[_windowSize];
        Span<float> imag = new float[_windowSize];
        for (var i = 0; i < _windowSize; i++)
        {
            real[i] = _accumulator[i] * _hannWindow[i];
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
        _frames.OnNext(new WaterfallFrame(magnitudesDb, binWidthHz, DateTimeOffset.UtcNow));
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
