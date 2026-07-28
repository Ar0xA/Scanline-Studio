namespace Yoniq.Core.Sstv;

/// <summary>Direct port of legacy <c>CSmooz</c> (`sstv.h:80-145`) — a fixed-size ring-buffer moving
/// average, used by AFC (<see cref="AfcTracker"/>) for both its short-term sync-tone-reading average
/// and its longer-term per-lock-event average.</summary>
internal sealed class MovingAverage
{
    private readonly double[] _buffer;
    private int _writeIndex;
    private int _count;

    public MovingAverage(int size)
    {
        _buffer = new double[Math.Max(1, size)];
    }

    /// <summary><c>CSmooz::Avg(d)</c> — pushes a new value and returns the average of all buffered
    /// values (fewer than the full size until the buffer first fills).</summary>
    public double Add(double value)
    {
        _buffer[_writeIndex] = value;
        _writeIndex = (_writeIndex + 1) % _buffer.Length;
        if (_count < _buffer.Length)
        {
            _count++;
        }

        double sum = 0;
        for (var i = 0; i < _count; i++)
        {
            sum += _buffer[i];
        }

        return sum / _count;
    }

    /// <summary><c>CSmooz::SetData(d)</c> — fills the entire buffer with one value: an instant
    /// full-strength seed, used the first time a real AFC lock succeeds so it isn't diluted by an
    /// otherwise mostly-empty moving average.</summary>
    public double Reset(double value)
    {
        Array.Fill(_buffer, value);
        _writeIndex = 0;
        _count = _buffer.Length;
        return value;
    }
}
