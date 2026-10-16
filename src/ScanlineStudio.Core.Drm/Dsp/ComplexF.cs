using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Drm.Dsp;

/// <summary>Single-precision complex value with the same memory layout as fftwf_complex
/// (re, im floats), for the float RX demodulator path (QSSTV drmrx/demodulator.cpp:14-15).
/// Double-precision code uses System.Numerics.Complex, which already has fftw_complex's layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ComplexF(float Real, float Imaginary)
{
    public static ComplexF operator +(ComplexF a, ComplexF b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);

    public static ComplexF operator -(ComplexF a, ComplexF b) => new(a.Real - b.Real, a.Imaginary - b.Imaginary);

    public static ComplexF operator *(ComplexF a, ComplexF b) =>
        new((a.Real * b.Real) - (a.Imaginary * b.Imaginary), (a.Real * b.Imaginary) + (a.Imaginary * b.Real));

    public static ComplexF operator *(ComplexF a, float s) => new(a.Real * s, a.Imaginary * s);

    public ComplexF Conjugate() => new(Real, -Imaginary);

    public float MagnitudeSquared() => (Real * Real) + (Imaginary * Imaginary);
}
