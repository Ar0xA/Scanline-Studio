namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CIIR</c>/<c>MakeIIR</c> (`fir.cpp`) — a Butterworth IIR biquad cascade
/// via bilinear transform, applied as a direct-form-II-like cascade. Per CLAUDE.md's "port first,
/// invent second" rule for DSP/codec math: this mirrors the legacy filter design and application
/// exactly rather than substituting a simpler filter, because the legacy PLL demodulator
/// (<see cref="PllFmDemodulator"/>) is tuned around this specific filter's transient response.
/// </summary>
internal sealed class IirFilter
{
    private double[] _a = [];
    private double[] _b = [];
    private double[] _z = [];
    private int _order;

    public void Design(double cutoffHz, double sampleRate, int order)
    {
        _order = order;
        _a = new double[order * 3];
        _b = new double[order * 2];
        _z = new double[order * 2];

        var wa = Math.Tan(Math.PI * cutoffHz / sampleRate);
        const double w0 = 1.0; // Butterworth only (legacy's Chebyshev branch is unused by the PLL)
        var n = (order & 1) + 1;

        var aIndex = 0;
        var bIndex = 0;
        for (var j = 1; j <= order / 2; j++, aIndex += 3, bIndex += 2, n += 2)
        {
            var zt = Math.Cos(n * Math.PI / (2 * order));
            _a[aIndex + 0] = 1 + wa * w0 * 2 * zt + wa * w0 * wa * w0;
            _a[aIndex + 1] = -2 * (wa * w0 * wa * w0 - 1) / _a[aIndex + 0];
            _a[aIndex + 2] = -(1.0 - wa * w0 * 2 * zt + wa * w0 * wa * w0) / _a[aIndex + 0];
            _b[bIndex + 0] = wa * w0 * wa * w0 / _a[aIndex + 0];
            _b[bIndex + 1] = 2 * _b[bIndex + 0];
        }

        if ((order & 1) != 0)
        {
            var j = order / 2;
            var ai = j * 3;
            var bi = j * 2;
            _a[ai + 0] = 1 + wa * w0;
            _a[ai + 1] = -(wa * w0 - 1) / _a[ai + 0];
            _b[bi + 0] = wa * w0 / _a[ai + 0];
            _b[bi + 1] = _b[bi + 0];
        }
    }

    public double Process(double input)
    {
        var d = input;
        var aIndex = 0;
        var bIndex = 0;
        var zIndex = 0;

        for (var i = 0; i < _order / 2; i++, aIndex += 3, bIndex += 2, zIndex += 2)
        {
            d += _z[zIndex + 0] * _a[aIndex + 1] + _z[zIndex + 1] * _a[aIndex + 2];
            var o = d * _b[bIndex + 0] + _z[zIndex + 0] * _b[bIndex + 1] + _z[zIndex + 1] * _b[bIndex + 0];
            _z[zIndex + 1] = _z[zIndex + 0];
            if (Math.Abs(d) < 1e-37)
            {
                d = 0.0;
            }

            _z[zIndex + 0] = d;
            d = o;
        }

        if ((_order & 1) != 0)
        {
            d += _z[zIndex + 0] * _a[aIndex + 1];
            var o = d * _b[bIndex + 0] + _z[zIndex + 0] * _b[bIndex + 0];
            if (Math.Abs(d) < 1e-37)
            {
                d = 0.0;
            }

            _z[zIndex + 0] = d;
            d = o;
        }

        return d;
    }
}
