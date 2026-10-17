// C# port of the sYCC and e-YCC conversions in OpenJPEG 2.5.4 src/bin/common/color.c
// (sycc_to_rgb, sycc444/422/420_to_rgb, color_sycc_to_rgb, color_esycc_to_rgb). Original notice:
//
// The copyright in this software is being made available under the 2-clauses
// BSD License, included below. This software may be subject to other third
// party and contributor rights, including patent rights, and no such rights
// are granted under this license.
//
// Copyright (c) 2002-2014, Universite catholique de Louvain (UCL), Belgium
// Copyright (c) 2002-2014, Professor Benoit Macq
// Copyright (c) 2001-2003, David Janssens
// Copyright (c) 2002-2003, Yannick Verschueren
// Copyright (c) 2003-2007, Francois-Olivier Devaux
// Copyright (c) 2003-2014, Antonin Descampe
// Copyright (c) 2005, Herve Drolon, FreeImage Team
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS `AS IS'
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.

namespace ScanlineStudio.Imaging.OpenJpeg;

/// <summary>Receives one converted pixel. Values are unsigned at component 0's precision, clamped to
/// [0, 2^prec − 1], as color.c leaves them.</summary>
internal interface IRgbSink
{
    void Set(int index, int r, int g, int b);
}

/// <summary>The sYCC/e-YCC → RGB conversions of OpenJPEG 2.5.4 <c>color.c</c>, read from spans and written
/// to a sink instead of replacing the image's component buffers. Expression widths follow the C exactly:
/// sYCC multiplies in double (<c>1.402 * (float)cr</c>), e-YCC in float with a +0.5 truncation. A chroma
/// buffer shorter than the loop expects throws <see cref="IndexOutOfRangeException"/>, never reads past it.</summary>
internal static class OpenJpegColor
{
    internal enum SyccLayout
    {
        CannotConvert,
        Yuv444,
        Yuv422,
        Yuv420,
    }

    /// <summary>color_sycc_to_rgb's subsampling dispatch (color.c:433-466).</summary>
    public static SyccLayout LayoutOf(uint dx0, uint dy0, uint dx1, uint dy1, uint dx2, uint dy2)
    {
        if (dx0 == 1 && dx1 == 2 && dx2 == 2 && dy0 == 1 && dy1 == 2 && dy2 == 2)
            return SyccLayout.Yuv420;
        if (dx0 == 1 && dx1 == 2 && dx2 == 2 && dy0 == 1 && dy1 == 1 && dy2 == 1)
            return SyccLayout.Yuv422;
        if (dx0 == 1 && dx1 == 1 && dx2 == 1 && dy0 == 1 && dy1 == 1 && dy2 == 1)
            return SyccLayout.Yuv444;
        return SyccLayout.CannotConvert;
    }

    /// <summary>color_esycc_to_rgb's guard (color.c:1107-1117), for an image of at least 3 components.</summary>
    public static bool CanConvertEsycc(uint dx0, uint dy0, uint dx1, uint dy1, uint dx2, uint dy2) =>
        dx0 == dx1 && dx0 == dx2 && dy0 == dy1 && dy0 == dy2;

    public static void Sycc444<TSink>(int prec, ReadOnlySpan<int> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr,
        int maxw, int maxh, ref TSink sink)
        where TSink : struct, IRgbSink
    {
        (int offset, int upb) = Range(prec);
        int max = checked(maxw * maxh);
        for (int i = 0; i < max; i++)
            SyccToRgb(offset, upb, y[i], cb[i], cr[i], i, ref sink);
    }

    public static void Sycc422<TSink>(int prec, ReadOnlySpan<int> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr,
        int maxw, int maxh, int comp12w, uint x0, ref TSink sink)
        where TSink : struct, IRgbSink
    {
        (int offset, int upb) = Range(prec);
        int yi = 0, ci = 0, o = 0;

        // If x0 is odd, the first column uses Cb/Cr = 0 (color.c:186).
        int offx = (int)(x0 & 1U);
        int loopmaxw = maxw - offx;
        for (int i = 0; i < maxh; i++)
        {
            int j;
            if (offx > 0)
                SyccToRgb(offset, upb, y[yi++], 0, 0, o++, ref sink);

            for (j = 0; j < (loopmaxw & ~1); j += 2)
            {
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                ci++;
            }

            if (j < loopmaxw)
            {
                if (j / 2 == comp12w)
                    SyccToRgb(offset, upb, y[yi], 0, 0, o, ref sink);
                else
                    SyccToRgb(offset, upb, y[yi], cb[ci], cr[ci], o, ref sink);
                yi++;
                o++;
                if (j / 2 < comp12w)
                    ci++;
            }
        }
    }

    public static void Sycc420<TSink>(int prec, ReadOnlySpan<int> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr,
        int maxw, int maxh, int comp12w, uint x0, uint y0, ref TSink sink)
        where TSink : struct, IRgbSink
    {
        (int offset, int upb) = Range(prec);
        int yi = 0, ci = 0, o = 0;

        int offx = (int)(x0 & 1U);
        int loopmaxw = maxw - offx;

        // If y0 is odd, the first line uses Cb/Cr = 0 (color.c:284).
        int offy = (int)(y0 & 1U);
        int loopmaxh = maxh - offy;

        if (offy > 0)
        {
            for (int j = 0; j < maxw; j++)
                SyccToRgb(offset, upb, y[yi++], 0, 0, o++, ref sink);
        }

        int i;
        for (i = 0; i < (loopmaxh & ~1); i += 2)
        {
            int j;
            int nyi = yi + maxw;
            int no = o + maxw;

            if (offx > 0)
            {
                SyccToRgb(offset, upb, y[yi++], 0, 0, o++, ref sink);
                // color.c:314 gives the second row's first pixel the current Cb/Cr, not 0; kept as-is.
                SyccToRgb(offset, upb, y[nyi++], cb[ci], cr[ci], no++, ref sink);
            }

            for (j = 0; j < (loopmaxw & ~1); j += 2)
            {
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                SyccToRgb(offset, upb, y[nyi++], cb[ci], cr[ci], no++, ref sink);
                SyccToRgb(offset, upb, y[nyi++], cb[ci], cr[ci], no++, ref sink);
                ci++;
            }

            if (j < loopmaxw)
            {
                if (j / 2 == comp12w)
                    SyccToRgb(offset, upb, y[yi], 0, 0, o, ref sink);
                else
                    SyccToRgb(offset, upb, y[yi], cb[ci], cr[ci], o, ref sink);
                yi++;
                o++;

                if (j / 2 == comp12w)
                    SyccToRgb(offset, upb, y[nyi], 0, 0, no, ref sink);
                else
                    SyccToRgb(offset, upb, y[nyi], cb[ci], cr[ci], no, ref sink);

                if (j / 2 < comp12w)
                    ci++;
            }

            yi += maxw;
            o += maxw;
        }

        if (i < loopmaxh)
        {
            int j;
            if (offx > 0)
                SyccToRgb(offset, upb, y[yi++], 0, 0, o++, ref sink);

            for (j = 0; j < (loopmaxw & ~1); j += 2)
            {
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                SyccToRgb(offset, upb, y[yi++], cb[ci], cr[ci], o++, ref sink);
                ci++;
            }

            if (j < loopmaxw)
            {
                if (j / 2 == comp12w)
                    SyccToRgb(offset, upb, y[yi], 0, 0, o, ref sink);
                else
                    SyccToRgb(offset, upb, y[yi], cb[ci], cr[ci], o, ref sink);
            }
        }
    }

    /// <summary>color_esycc_to_rgb (color.c:1100-1175). <paramref name="max"/> is component 0's w·h.</summary>
    public static void Esycc<TSink>(int prec, ReadOnlySpan<int> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr,
        bool sign1, bool sign2, int max, ref TSink sink)
        where TSink : struct, IRgbSink
    {
        (int flipValue, int maxValue) = Range(prec);
        for (int i = 0; i < max; i++)
        {
            int yv = y[i];
            int cbv = cb[i];
            int crv = cr[i];
            if (!sign1)
                cbv -= flipValue;
            if (!sign2)
                crv -= flipValue;

            // Each cast forces single precision per operation, as C with FLT_EVAL_METHOD 0.
            int r = Clamp((int)(float)((float)((float)((float)yv - (float)((float)0.0000368 * (float)cbv))
                + (float)((float)1.40199 * (float)crv)) + (float)0.5), maxValue);
            int g = Clamp((int)(float)((float)((float)((float)((float)1.0003 * (float)yv) - (float)((float)0.344125 * (float)cbv))
                - (float)((float)0.7141128 * (float)crv)) + (float)0.5), maxValue);
            int b = Clamp((int)(float)((float)((float)((float)((float)0.999823 * (float)yv) + (float)((float)1.77204 * (float)cbv))
                - (float)((float)0.000008 * (float)crv)) + (float)0.5), maxValue);
            sink.Set(i, r, g, b);
        }
    }

    /// <summary>sycc_to_rgb (color.c:74-104): double multiplies of a float-cast chroma, truncated to int.</summary>
    private static void SyccToRgb<TSink>(int offset, int upb, int y, int cb, int cr, int index, ref TSink sink)
        where TSink : struct, IRgbSink
    {
        cb -= offset;
        cr -= offset;
        int r = Clamp(y + (int)(1.402 * (double)(float)cr), upb);
        int g = Clamp(y - (int)((0.344 * (double)(float)cb) + (0.714 * (double)(float)cr)), upb);
        int b = Clamp(y + (int)(1.772 * (double)(float)cb), upb);
        sink.Set(index, r, g, b);
    }

    private static int Clamp(int value, int upb) => value < 0 ? 0 : value > upb ? upb : value;

    /// <summary>offset = 2^(prec−1), upb = 2^prec − 1, from component 0 (color.c:113-115, 1104-1105).</summary>
    private static (int Offset, int Upb) Range(int prec)
    {
        // 2^31 − 1 overflows int in the C; refuse rather than reproduce undefined behaviour.
        if (prec is < 1 or > 30)
            throw new InvalidDataException($"Cannot convert YCC at {prec}-bit precision.");
        return (1 << (prec - 1), (1 << prec) - 1);
    }
}
