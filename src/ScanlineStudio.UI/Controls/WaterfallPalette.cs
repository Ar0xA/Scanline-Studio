namespace ScanlineStudio.UI.Controls;

/// <summary>Fixed SDR-style heatmap gradient (WSJT-X/SDR++/GQRX convention) replacing legacy's own
/// flat 2-color linear Low/High interpolation (`Main.cpp:806-812`'s default `clBlack`/`clWhite`,
/// `ComLib.cpp:338-361`'s `InitColorTable`) -- spec/09-ui.md exempts this visualization from strict
/// legacy-port fidelity ("worth real design attention... not decoration"), and spec/14-roadmap.md's
/// own waterfall backlog entry explicitly invites a nicer multi-hue gradient ("QSSTV does a nicer
/// multi-hue heatmap gradient... worth a look if/when color rendering is built"). The two ends of
/// the gradient still carry legacy's "Low"/"High" semantic (weakest signal -> near-black, strongest
/// -> red) -- verified against `Main.cpp:3490-3497`'s `ColorTable[127-d]` lookup, where `d` rises
/// with signal strength: `d`=0 (weakest) resolves to index 127 = the Low color, `d`=127 (strongest)
/// resolves to index 0 = the High color. Legacy's own internal table index order is inverted; the
/// EXTERNAL weak-signal/strong-signal semantic is what's preserved here, not that inversion.
///
/// Pure data transform, independently testable without a real Avalonia render pass -- kept out of
/// <see cref="WaterfallControl"/> itself for exactly that reason. Public (not internal) specifically
/// so <c>ScanlineStudio.UI.Tests</c> can unit-test it directly -- this project has no
/// <c>InternalsVisibleTo</c> wired up anywhere (checked: `LuminanceClipStatistics`, this class's own
/// closest precedent, is `internal` and consequently has no direct unit test at all).</summary>
public static class WaterfallPalette
{
    private static readonly (double Position, byte R, byte G, byte B)[] Stops =
    [
        (0.00, 0x0A, 0x0A, 0x14), // near-black
        (0.15, 0x00, 0x00, 0xA0), // deep blue
        (0.35, 0x00, 0xB4, 0xDC), // cyan
        (0.55, 0x00, 0xC8, 0x50), // green
        (0.75, 0xE6, 0xDC, 0x00), // yellow
        (1.00, 0xFF, 0x3C, 0x28), // red
    ];

    /// <summary><paramref name="normalized"/> is clamped to [0,1] before lookup -- 0 = weakest
    /// signal (Low), 1 = strongest (High).</summary>
    public static (byte R, byte G, byte B) Lerp(double normalized)
    {
        var n = Math.Clamp(normalized, 0.0, 1.0);
        for (var i = 1; i < Stops.Length; i++)
        {
            var (posB, _, _, _) = Stops[i];
            if (n > posB && i != Stops.Length - 1)
            {
                continue;
            }

            var (posA, rA, gA, bA) = Stops[i - 1];
            var (_, rB, gB, bB) = Stops[i];
            var span = posB - posA;
            var t = span > 0 ? Math.Clamp((n - posA) / span, 0.0, 1.0) : 0.0;
            return (
                (byte)Math.Round(rA + ((rB - rA) * t)),
                (byte)Math.Round(gA + ((gB - gA) * t)),
                (byte)Math.Round(bA + ((bB - bA) * t)));
        }

        var last = Stops[^1];
        return (last.R, last.G, last.B);
    }
}
