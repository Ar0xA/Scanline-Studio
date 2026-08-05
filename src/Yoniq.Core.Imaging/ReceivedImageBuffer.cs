using SixLabors.ImageSharp;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Imaging;

/// <summary>The "application-layer adapter" spec/07-image-pipeline.md describes — subscribes to
/// <see cref="ISstvDecoder.LineDecoded"/> itself, rather than something in <c>Yoniq.Application</c>
/// doing the wiring. Layering-legal despite living in <c>Yoniq.Core.Imaging</c>: <see cref="ISstvDecoder"/>
/// is in <c>Yoniq.Abstractions.Sstv</c>, below `Yoniq.Application` in spec/01-architecture.md's
/// diagram, same as this project — no Core-to-Core edge is created. <c>Yoniq.Core.Imaging</c> must
/// never gain a reference to <c>Yoniq.Core.Sstv</c> itself; that (not this class) is what would make
/// this placement illegal.
///
/// <b>Whole-image snapshot, not per-line</b> (a real bug caught in plan review before this shipped):
/// <see cref="ISstvDecoder.LineDecoded"/> fires with only the *starting* row of a
/// multi-row transmission unit for the paired-line families (PD/MP, RM8/RM12 —
/// `IScanlineDecoder.RowsPerTransmissionLine` is 2 for those, not exposed on the public
/// <see cref="SstvModeDefinition"/>). <see cref="DecodedImageUpdate.Image"/> is always the *whole*
/// live canvas (`AnalogFmSstvDecoder`'s private `MutableImageSource` wraps the full
/// mode-sized pixel array), so snapshotting the entire image on every event — not just the reported
/// row — is both simplest and correct, and cheap (bounded by one video frame's own pixel count).
///
/// <b>Snapshot contract</b> (Phase-3 plan decision #5): <see cref="Current"/> always returns a
/// defensive copy, never a view into the decoder's own live/mutable buffer.</summary>
public sealed class ReceivedImageBuffer : IReceivedImageBuffer
{
    private static readonly IImageSource EmptyImage = new ArrayImageSource(1, 1, [new Rgb24(0, 0, 0)]);

    private readonly object _gate = new();
    private IImageSource _current = EmptyImage;

    public ReceivedImageBuffer(ISstvDecoder decoder)
    {
        decoder.LineDecoded += OnLineDecoded;
        decoder.DecodeRestarted += OnDecodeRestarted;
    }

    public IImageSource Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action? Updated;

    public Task SaveAsync(string path, CancellationToken ct = default)
    {
        var snapshot = Current;
        return Task.Run(
            () =>
            {
                using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(snapshot.Width, snapshot.Height);
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < snapshot.Height; y++)
                    {
                        var sourceRow = snapshot.GetScanline(y);
                        var destinationRow = accessor.GetRowSpan(y);
                        for (var x = 0; x < snapshot.Width; x++)
                        {
                            var pixel = sourceRow[x];
                            destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                        }
                    }
                });
                image.Save(path);
            },
            ct);
    }

    private void OnLineDecoded(DecodedImageUpdate update)
    {
        var snapshot = Snapshot(update.Image);
        lock (_gate)
        {
            _current = snapshot;
        }

        Updated?.Invoke();
    }

    private void OnDecodeRestarted(SstvModeDefinition abandonedMode)
    {
        lock (_gate)
        {
            _current = EmptyImage;
        }

        Updated?.Invoke();
    }

    private static ArrayImageSource Snapshot(IImageSource source)
    {
        var pixels = new Rgb24[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            source.GetScanline(y).CopyTo(pixels.AsSpan(y * source.Width, source.Width));
        }

        return new ArrayImageSource(source.Width, source.Height, pixels);
    }
}
