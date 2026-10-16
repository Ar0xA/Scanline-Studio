namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Float LU factorization with scaled partial pivoting and the matrix inverse built from it, for the
/// channel estimator's Wiener matrix inversion (drmrx/demodulator.cpp:792-810). Our own implementation: QSSTV
/// calls Numerical Recipes <c>ludcmp</c>/<c>lubksb</c> (drmrx/ludcmp.cpp, lubksb.cpp, nrutil.cpp), which are
/// not free software and are not ported.</summary>
/// <remarks>
/// <para><b>Algorithm.</b> Right-looking Gaussian elimination (Doolittle, unit lower triangle) in place on a
/// row-major n×n float matrix. Scaled partial pivoting: each row's scale is 1/max|a(i,·)| of the input
/// matrix; at step k the pivot is the row i ≥ k with the largest scale·|a(i,k)|, the last such row on a tie;
/// the scale moves with its row. Multipliers are formed as a(i,k) · (1/a(k,k)) with the reciprocal rounded to
/// float, and an exactly zero pivot is replaced by 1e-20.</para>
/// <para><b>Why this matches QSSTV.</b> Each element sees the same float operations in the same order as in
/// any Crout/Doolittle variant with this pivot rule (a(i,j) minus the products for k = 0, 1, … in turn), so the
/// factors, and the inverse built column by column with forward and back substitution, are the values QSSTV
/// computes. The golden tests compare the resulting Wiener weights with QSSTV's for all six mode/occupancy
/// pairs.</para>
/// <para>Not thread-safe per buffer; allocation-free.</para>
/// </remarks>
internal static class DrmLuSolver
{
    private const float ZeroPivotReplacement = 1.0e-20f;

    /// <summary>Factorizes <paramref name="a"/> (n×n, row stride <paramref name="stride"/>) in place into unit-lower L
    /// and upper U of the row-permuted matrix. <paramref name="pivot"/>[k] is the row swapped with row k at step k.</summary>
    /// <exception cref="InvalidOperationException">A row is entirely zero.</exception>
    public static void Decompose(Span<float> a, int n, int stride, Span<int> pivot, Span<float> scaleScratch)
    {
        Span<float> scale = scaleScratch[..n];
        for (int i = 0; i < n; i++)
        {
            float big = 0.0f;
            Span<float> row = a.Slice(i * stride, n);
            for (int j = 0; j < n; j++)
            {
                float v = MathF.Abs(row[j]);
                if (v > big)
                    big = v;
            }

            if (big == 0.0f)
                throw new InvalidOperationException("Singular matrix: a row is entirely zero.");
            scale[i] = (float)(1.0 / big);
        }

        for (int k = 0; k < n; k++)
        {
            float best = 0.0f;
            int p = k;
            for (int i = k; i < n; i++)
            {
                float score = scale[i] * MathF.Abs(a[i * stride + k]);
                if (score >= best)
                {
                    best = score;
                    p = i;
                }
            }

            if (p != k)
            {
                Span<float> rp = a.Slice(p * stride, n), rk = a.Slice(k * stride, n);
                for (int j = 0; j < n; j++)
                    (rp[j], rk[j]) = (rk[j], rp[j]);
                scale[p] = scale[k];
            }

            pivot[k] = p;
            if (a[k * stride + k] == 0.0f)
                a[k * stride + k] = ZeroPivotReplacement;
            if (k == n - 1)
                break;

            float inv = (float)(1.0 / a[k * stride + k]);
            Span<float> pivotRow = a.Slice(k * stride, n);
            for (int i = k + 1; i < n; i++)
            {
                Span<float> row = a.Slice(i * stride, n);
                float m = row[k] * inv;
                row[k] = m;
                for (int j = k + 1; j < n; j++)
                    row[j] -= m * pivotRow[j];
            }
        }
    }

    /// <summary>Solves the factorized system for one right-hand side in place: permute, forward substitution
    /// with the unit lower triangle, back substitution with the upper triangle.</summary>
    public static void Solve(ReadOnlySpan<float> lu, int n, int stride, ReadOnlySpan<int> pivot, Span<float> b)
    {
        for (int k = 0; k < n; k++)
        {
            int p = pivot[k];
            if (p != k)
                (b[p], b[k]) = (b[k], b[p]);
        }

        for (int i = 1; i < n; i++)
        {
            float sum = b[i];
            ReadOnlySpan<float> row = lu.Slice(i * stride, i);
            for (int j = 0; j < i; j++)
                sum -= row[j] * b[j];
            b[i] = sum;
        }

        for (int i = n - 1; i >= 0; i--)
        {
            float sum = b[i];
            ReadOnlySpan<float> row = lu.Slice(i * stride, n);
            for (int j = i + 1; j < n; j++)
                sum -= row[j] * b[j];
            b[i] = sum / row[i];
        }
    }

    /// <summary>Inverts <paramref name="a"/> (destroyed) into <paramref name="inverse"/> column by column, as
    /// demodulator.cpp:800-807 does with the factors. Both are n×n with row stride <paramref name="stride"/>.</summary>
    public static void Invert(Span<float> a, int n, int stride, Span<float> inverse, Span<int> pivot, Span<float> scratch)
    {
        Decompose(a, n, stride, pivot, scratch);
        Span<float> col = scratch[..n];
        for (int j = 0; j < n; j++)
        {
            col.Clear();
            col[j] = 1.0f;
            Solve(a, n, stride, pivot, col);
            for (int i = 0; i < n; i++)
                inverse[i * stride + j] = col[i];
        }
    }
}
