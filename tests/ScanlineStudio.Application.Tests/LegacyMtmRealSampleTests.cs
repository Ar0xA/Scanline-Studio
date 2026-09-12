using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>Runs the real, production <c>LegacyMtmReader</c> against the actual `.mtm` sample files
/// already sitting in the local, gitignored `yoniq-old/YONIQ-main/` clone -- proving the C#
/// implementation matches real legacy-authored bytes, not just the hand-authored synthetic fixtures
/// <c>LegacyMtmReaderTests</c> exercises. This mirrors exactly what the Python reference parser
/// already proved during the ORIGINAL reverse-engineering pass, quoting its own historical result
/// verbatim (`docs/mtm-binary-format.md`: "12 of 12 parse every byte, landing exactly on end-of-file"
/// -- that pass covered only the root `TemplateDir` files, not the `Stock/` copies this file's own
/// broader candidate list also checks; see <see cref="RequiresLocalYoniqCloneFactAttribute"/> for the
/// current count) -- now proving the SHIPPED reader does the same, not a prototype.
///
/// <para><b>No file here is ever committed.</b> Every sample lives only in the local `yoniq-old/`
/// clone, and this file contains no literal legacy content of its own (no copied text, font names, or
/// byte fragments) -- only structural assertions (element counts, tags, clean EOF) that reference
/// what a sample contains without reproducing it. <c>LICENSES.md</c> confirmed no new entry is needed
/// for that reason (a `yoniq-auditor` plan-review finding).</para></summary>
public sealed class LegacyMtmRealSampleTests
{
    [RequiresLocalYoniqCloneFact]
    public void ReadTemplate_EveryRealLocalSample_ParsesToAByteExactCleanEndOfFile()
    {
        var yoniqOldDirectory = LegacyMtmSampleFiles.FindYoniqOldDirectory()
            ?? throw new InvalidOperationException("RequiresLocalYoniqCloneFact should have skipped otherwise.");
        var samples = LegacyMtmSampleFiles.ListSampleFiles(yoniqOldDirectory);

        Assert.NotEmpty(samples); // the attribute already confirmed the clone exists; this confirms it has the expected files too

        var failures = new List<string>();
        foreach (var path in samples)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                LegacyMtmReader.ReadTemplate(bytes, out var remaining);
                if (remaining != 0)
                {
                    failures.Add($"{Path.GetFileName(path)}: {remaining} bytes left unread after parsing (not a clean EOF).");
                }
            }
            catch (LegacyMtmFormatException ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {samples.Count} real local samples failed:\n{string.Join("\n", failures)}");
    }

    [RequiresLocalYoniqCloneFact]
    public void ReadTemplate_RealLocalSamples_HaveEveryCM_PICAtTypeZero()
    {
        // Pins the exact fact that makes this test suite's synthetic-BMP tests necessary in the first
        // place: no local real sample carries a populated bitmap (docs/mtm-binary-format.md's own
        // stated gap). If this ever starts failing, a real image-bearing .mtm has appeared locally --
        // worth a deliberate look at LegacyMtmImportAdapter's image path against real bytes, not a
        // regression to silently accept.
        var yoniqOldDirectory = LegacyMtmSampleFiles.FindYoniqOldDirectory()!;
        var samples = LegacyMtmSampleFiles.ListSampleFiles(yoniqOldDirectory);

        foreach (var path in samples)
        {
            var bytes = File.ReadAllBytes(path);
            var root = LegacyMtmReader.ReadTemplate(bytes);
            AssertNoPopulatedBitmap(root, path);
        }
    }

    private static void AssertNoPopulatedBitmap(MtmGroupElement group, string path)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case MtmPictureElement pic:
                    Assert.True(pic.Bitmap is null, $"{Path.GetFileName(path)} has a populated CM_PIC bitmap -- update this test's own expectation and give LegacyMtmImportAdapter's image path a real look.");
                    break;
                case MtmTitleElement { Bitmap: not null }:
                    Assert.Fail($"{Path.GetFileName(path)} has a populated CM_TITLE bitmap -- update this test's own expectation.");
                    break;
                case MtmGroupElement nested:
                    AssertNoPopulatedBitmap(nested, path);
                    break;
            }
        }
    }

    [RequiresLocalYoniqCloneFact]
    public async Task ImportLegacyMtmAsync_EveryRealLocalSample_ImportsThroughTheFullPipelineWithoutThrowing()
    {
        // The end-to-end pipeline, not just the reader -- LegacyMtmImportAdapter's mapping rules and
        // TemplateStore's own asset/manifest writing, against real bytes. Uses a temp directory
        // (templatesRootOverride) so this never touches a real user's actual template library.
        var yoniqOldDirectory = LegacyMtmSampleFiles.FindYoniqOldDirectory()!;
        var samples = LegacyMtmSampleFiles.ListSampleFiles(yoniqOldDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"scanline-mtm-real-sample-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // REAL image I/O, not the fakes -- every real local sample's own CM_PIC/CM_TITLE bitmap
            // is confirmed empty (this file's own AtTypeZero test above), but the companion-image
            // lookup (BACKLOG.md task 12) found REAL TxStock*.jpg/Current.bmp files sitting next to
            // several of these real .mtm samples, which this pipeline now decodes for real -- a fake
            // loader would throw "no fake source configured" for exactly the files that make this
            // test worth running for real.
            var store = new TemplateStore(
                new ImageSourceWriter(), new ImageFileLoader(), new FakeTransmitImagePreparer(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TemplateStore>.Instance, tempRoot);

            var failures = new List<string>();
            foreach (var path in samples)
            {
                try
                {
                    var result = await store.ImportLegacyMtmAsync(path);
                    Assert.False(string.IsNullOrEmpty(result.TemplateId));
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0, $"{failures.Count} of {samples.Count} real local samples failed the full import pipeline:\n{string.Join("\n", failures)}");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
