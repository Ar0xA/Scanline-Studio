using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// `BACKLOG.md` PA-2 / TT1-5 — the per-mode TX channel-order table, transcribed from legacy
/// `Main.cpp`'s own `TMmsstv::Line*` functions.
///
/// <para><b>Why this exists.</b> Channel ORDER is the literal Scottie-incident failure mode
/// (`CLAUDE.md` §4): an earlier port inferred Scottie's TX order from RX branch widths and got it
/// wrong, and round-trip testing did not catch it because the encoder and decoder agreed with each
/// other while both were wrong about reality. The golden-vector fixtures that were meant to guard
/// this do NOT currently run — all 11 `TxCapture/*.provenance` files read
/// `UNKNOWN-STALE-PENDING-RECAPTURE` and `StaleFixtureTheoryAttribute` skips the comparison — so
/// until they are recaptured, real TX channel-order coverage is ZERO. This table is what stands in
/// the gap, and unlike a capture it needs no hardware.</para>
///
/// <para><b>Transcription, not inference.</b> Every row below was read out of the legacy function
/// body, and the dispatch that selects it was read out of `Main.cpp`'s TX switch (around `:7060`).
/// Legacy's own `// Y` / `// R-Y` / `// G` comments mark each scan loop, so the order is stated by
/// the source rather than deduced from timings. 43 modes map to 14 distinct functions.</para>
///
/// <para><b>One fixture per FUNCTION pins order, not one per mode.</b> The parameters those
/// functions take (`tw`, `S`, `P`, `C`, `ts`) are durations and porch widths — none of them selects
/// or reorders a channel. So the real recapture gap is at most 14, not 43. This is exactly the
/// "does the mapping say 5 fixtures or 30" question PA-2 asked, answered: **14 functions, of which
/// the 11 stale fixtures cover 10 distinct ones, leaving 4 functions never covered at all** —
/// `LineSC2180`, `LineP`, `LineMP`, `LineMC`.</para>
///
/// <para><b>Both halves of the incident are covered.</b> The original bug was sync-first R,G,B
/// against the real separator-G, separator-B, sync, separator-R — wrong in channel order AND in sync
/// placement. Comparing scan names alone catches only the first half, because
/// <c>OfType&lt;ScanSegment&gt;()</c> filters every sync out. So each row also carries how many scans
/// precede the line's primary sync. Both are mutation-gated: renaming a scan channel and moving
/// Scottie's sync to the head each fail a row.</para>
///
/// <para><b>A per-function golden capture would not make this file redundant.</b> A capture pins one
/// function's internal order. This table pins the mode→function DISPATCH — that `martin-m1` routes
/// to `LineMRT` and `scottie-s1` to `LineSCT`, which have identical channel order and different sync
/// placement. That is precisely the pair where inferring one family from another goes wrong.</para>
///
/// <para><b>What this does NOT prove.</b> Three limits, stated so nobody over-reads a green run:
/// it compares the port's registry against legacy SOURCE rather than legacy's emitted audio, so a
/// shared misreading of the source still passes — the golden captures remain the only cure, and this
/// table makes their absence survivable, not acceptable. It does not pin segment DURATIONS, only
/// order and sync position. And for `rm8`/`rm12`/`robot-36` the encoders select channels
/// positionally rather than by name (`MonoAveragedPairedScanlineEncoder`, `RobotScanlineEncoder`),
/// so for those three rows the name is documentation while the count and position are the
/// load-bearing facts; the other 40 rows do dispatch on the name.</para>
/// </summary>
public sealed class LegacyTxChannelOrderTests
{
    /// <summary>
    /// Legacy TX scan-channel order per mode. Source: `Main.cpp`'s `TMmsstv::Line*` bodies, with the
    /// legacy function named per row so any dispute is one grep away.
    /// </summary>
    public static readonly TheoryData<string, string, string[], int?> LegacyOrders = new()
    {
        // LineR24 (:6535) -- Y, R-Y, B-Y. Sync 1200/6ms, porch 1500/2ms.
        { "r24", "LineR24", ["Y", "RY", "BY"], 0 },

        // LineR36 (:6558) -- Y then ONE chroma channel, alternating R-Y/B-Y by line parity
        // (`mp->m_wLine & 1`). The port models the alternation in its decoder, so the registry
        // carries the single chroma slot.
        { "robot-36", "LineR36", ["Y", "C"], 0 },

        // LineR72 (:6581) -- Y, R-Y, B-Y.
        { "robot-72", "LineR72", ["Y", "RY", "BY"], 0 },

        // LineAVT (:6603) -- R, G, B, no sync (AVT is syncless by design).
        { "avt", "LineAVT", ["R", "G", "B"], null },

        // LineSCT (:6620) -- G, B, then SYNC, then R. THE Scottie incident: the 1200/9ms sync sits
        // BETWEEN the B and R scans, not at the head of the line. Separator-G, separator-B,
        // sync-in-middle, separator-R -- exactly what CLAUDE.md §4 records.
        { "scottie-s1", "LineSCT", ["G", "B", "R"], 2 },
        { "scottie-s2", "LineSCT", ["G", "B", "R"], 2 },
        { "scottie-dx", "LineSCT", ["G", "B", "R"], 2 },

        // LineMRT (:6642) -- G, B, R with the sync at the HEAD, unlike LineSCT. Same channel order,
        // different sync placement: the pair that makes "infer one family from another" unsafe.
        { "martin-m1", "LineMRT", ["G", "B", "R"], 0 },
        { "martin-m2", "LineMRT", ["G", "B", "R"], 0 },

        // LineSC2180 (:6665) -- R, G, B.
        { "sc2-180", "LineSC2180", ["R", "G", "B"], 0 },
        { "sc2-120", "LineSC2180", ["R", "G", "B"], 0 },
        { "sc2-60", "LineSC2180", ["R", "G", "B"], 0 },

        // LinePD (:6686) -- Y(odd), R-Y, B-Y, Y(even). Two image rows per transmission line.
        { "pd50", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd90", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd120", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd160", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd180", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd240", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },
        { "pd290", "LinePD", ["Y1", "RY", "BY", "Y2"], 0 },

        // LineP (:6710) -- R, G, B at 640 px wide, separator before each channel.
        { "p3", "LineP", ["R", "G", "B"], 0 },
        { "p5", "LineP", ["R", "G", "B"], 0 },
        { "p7", "LineP", ["R", "G", "B"], 0 },

        // LineMR (:6757) -- Y, R-Y, B-Y. Serves BOTH the MR and ML families, nine modes on one
        // function body.
        { "mr73", "LineMR", ["Y", "RY", "BY"], 0 },
        { "mr90", "LineMR", ["Y", "RY", "BY"], 0 },
        { "mr115", "LineMR", ["Y", "RY", "BY"], 0 },
        { "mr140", "LineMR", ["Y", "RY", "BY"], 0 },
        { "mr175", "LineMR", ["Y", "RY", "BY"], 0 },
        { "ml180", "LineMR", ["Y", "RY", "BY"], 0 },
        { "ml240", "LineMR", ["Y", "RY", "BY"], 0 },
        { "ml280", "LineMR", ["Y", "RY", "BY"], 0 },
        { "ml320", "LineMR", ["Y", "RY", "BY"], 0 },

        // LineMP (:6733) -- Y(odd), R-Y, B-Y, Y(even), same shape as LinePD.
        { "mp73", "LineMP", ["Y1", "RY", "BY", "Y2"], 0 },
        { "mp115", "LineMP", ["Y1", "RY", "BY", "Y2"], 0 },
        { "mp140", "LineMP", ["Y1", "RY", "BY", "Y2"], 0 },
        { "mp175", "LineMP", ["Y1", "RY", "BY", "Y2"], 0 },

        // LineRM (:6785) -- monochrome, ONE Y scan per transmission line despite TWO `// Y` loops.
        // The first loop only READS row N into Y[]; then m_wLine++; then the second loop reads row
        // N+1 and writes `(YY + Y[x]) / 2` -- two image rows averaged into a single transmitted
        // scan. Counting the loop comments instead of the Write calls gives two, and is wrong.
        { "rm8", "LineRM", ["Y"], 0 },
        { "rm12", "LineRM", ["Y"], 0 },

        // LineMN (:6803) -- Y(odd), R-Y, B-Y, Y(even) on the NARROW frequency plan
        // (NARROW_SYNC/NARROW_LOW, ColorToFreqNarrow).
        { "mn73", "LineMN", ["Y1", "RY", "BY", "Y2"], 0 },
        { "mn110", "LineMN", ["Y1", "RY", "BY", "Y2"], 0 },
        { "mn140", "LineMN", ["Y1", "RY", "BY", "Y2"], 0 },

        // LineMC (:6827) -- R, G, B on the narrow plan.
        { "mc110", "LineMC", ["R", "G", "B"], 0 },
        { "mc140", "LineMC", ["R", "G", "B"], 0 },
        { "mc180", "LineMC", ["R", "G", "B"], 0 },
    };

    [Theory]
    [MemberData(nameof(LegacyOrders))]
    public void PortScanChannelOrder_MatchesLegacyLineFunction(
        string modeId, string legacyFunction, string[] expected, int? scansBeforePrimarySync)
    {
        var mode = SstvModeRegistry.All.SingleOrDefault(m => m.Id == modeId);
        Assert.True(mode is not null, $"Mode '{modeId}' is not in SstvModeRegistry -- the table below is transcribed from legacy {legacyFunction}, so either the id changed or a mode was dropped.");

        var actual = mode!.LineSegments.OfType<ScanSegment>().Select(s => s.ChannelName).ToArray();

        Assert.True(
            expected.SequenceEqual(actual),
            $"[{modeId}] channel order disagrees with legacy {legacyFunction}. " +
            $"Legacy: [{string.Join(", ", expected)}]. Port: [{string.Join(", ", actual)}].");

        AssertPrimarySyncPlacement(mode, legacyFunction, scansBeforePrimarySync);
    }

    /// <summary>
    /// The other half of the Scottie incident. Channel order alone does not catch it: the original
    /// bug was <b>sync-first R,G,B</b> against the real separator-G, separator-B, sync, separator-R,
    /// and a table that only compares scan names passes either way because
    /// <c>OfType&lt;ScanSegment&gt;()</c> filters every sync out.
    ///
    /// <para>Expressed as ONE number per mode — how many scans precede the line's primary sync —
    /// because that is the fact legacy states plainly and it is exactly what moving the sync breaks.
    /// It is 2 for every <c>LineSCT</c> mode, 0 for the other eleven functions that open with a
    /// sync, and null for <c>LineAVT</c>, which is syncless by design.</para>
    ///
    /// <para>The primary sync is the LONGEST <see cref="SyncSegment"/> in the line. Verified to pick
    /// the right one for all 14 functions: the separators and porches are always shorter than the
    /// real sync, with the narrowest margin at <c>LineMRT</c> (4.862 ms against 0.572 ms).</para>
    /// </summary>
    private static void AssertPrimarySyncPlacement(SstvModeDefinition mode, string legacyFunction, int? expected)
    {
        var syncs = mode.LineSegments.OfType<SyncSegment>().ToArray();

        if (expected is null)
        {
            Assert.True(
                syncs.Length == 0,
                $"[{mode.Id}] legacy {legacyFunction} writes no sync at all, but the port has {syncs.Length}.");
            return;
        }

        Assert.True(syncs.Length > 0, $"[{mode.Id}] legacy {legacyFunction} opens with a sync, but the port has none.");

        var primary = syncs.MaxBy(s => s.DurationMs)!;
        var actual = mode.LineSegments
            .TakeWhile(segment => !ReferenceEquals(segment, primary))
            .OfType<ScanSegment>()
            .Count();

        Assert.True(
            expected == actual,
            $"[{mode.Id}] primary sync is in the wrong place against legacy {legacyFunction}. " +
            $"Legacy writes it after {expected} scan(s); the port has it after {actual}. " +
            "This is the Scottie failure class -- channel order can be right while the sync is not.");
    }

    [Fact]
    public void EveryRegisteredMode_HasALegacyChannelOrderRow()
    {
        // The table is only a guard if it is complete. A mode added to the registry without a
        // transcribed legacy row would otherwise silently escape this file entirely -- the same
        // "the guard exists but does not run" shape as the stale TxCapture fixtures.
        var tabled = LegacyOrders.Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
        var registered = SstvModeRegistry.All.Select(m => m.Id).ToArray();

        var missing = registered.Where(id => !tabled.Contains(id)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{missing.Length} registered mode(s) have no legacy channel-order row: {string.Join(", ", missing)}. " +
            "Transcribe the order from that mode's own Main.cpp Line* function -- do NOT copy a sibling mode's row.");

        var stale = tabled.Where(id => !registered.Contains(id, StringComparer.Ordinal)).ToArray();
        Assert.True(stale.Length == 0, $"Table rows for modes no longer registered: {string.Join(", ", stale)}.");
    }
}
