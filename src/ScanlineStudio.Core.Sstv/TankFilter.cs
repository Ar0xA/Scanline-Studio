namespace ScanlineStudio.Core.Sstv;

/// <summary>Direct port of legacy <c>CIIRTANK</c> (`fir.cpp:40-74`, `fir.h:139-150`) — a compact
/// 2nd-order resonant IIR ("tank circuit") bandpass filter, distinct from <see cref="IirFilter"/>'s
/// Butterworth cascade. Used as the resonant stage of the fixed 1200Hz sync-tone amplitude-envelope
/// detector (see <see cref="SyncEnvelopeDetector"/>) that Auto Slant needs to find where the sync
/// pulse falls within each line.</summary>
internal sealed class TankFilter
{
    private double _z1;
    private double _z2;
    private double _a0;
    private double _b1;
    private double _b2;

    /// <summary><c>CIIRTANK::SetFreq</c> (`fir.cpp:46-63`).</summary>
    public void SetFreq(double frequencyHz, double sampleRate, double bandwidthHz)
    {
        _b1 = 2 * Math.Exp(-Math.PI * bandwidthHz / sampleRate) * Math.Cos(2 * Math.PI * frequencyHz / sampleRate);
        _b2 = -Math.Exp(-2 * Math.PI * bandwidthHz / sampleRate);
        _a0 = bandwidthHz != 0
            ? Math.Sin(2 * Math.PI * frequencyHz / sampleRate) / (sampleRate / 6.0 / bandwidthHz)
            : Math.Sin(2 * Math.PI * frequencyHz / sampleRate);
    }

    /// <summary><c>CIIRTANK::Do</c> (`fir.cpp:65-74`).</summary>
    public double Process(double input)
    {
        var d = input * _a0;
        d += _z1 * _b1;
        d += _z2 * _b2;
        _z2 = _z1;
        if (Math.Abs(d) < 1e-37)
        {
            d = 0.0;
        }

        _z1 = d;
        return d;
    }
}
