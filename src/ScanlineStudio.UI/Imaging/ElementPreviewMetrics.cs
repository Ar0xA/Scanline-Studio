using Avalonia;

namespace ScanlineStudio.UI.Imaging;

/// <summary>Maps final output pixels into the editing canvas. OutputOrigin is the element's
/// top-left in final image pixels, including crop projection and letterbox padding.</summary>
public sealed record ElementPreviewMetrics(double ImageHeight, Size PixelSize, Point OutputOrigin);
