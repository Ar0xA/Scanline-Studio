namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>The transport ID sequence for "bsr.bin" objects. QSSTV keeps it in a process-wide static
/// that starts at 2 and advances mod 3 on every SetMOTObject of a BSR, including the one at end of
/// transmission (DABMOT.cpp:56,98-102). Here it is per-transmitter state with the same sequence:
/// the first BSR goes out as 0 and its end-of-transmission blocks as 1. Not thread-safe.</summary>
internal sealed class BsrTransportIdCounter
{
    private int _id = 2;

    /// <summary>The last ID handed out (2 before the first call).</summary>
    public int Current => _id;

    /// <summary>Advances and returns the new ID.</summary>
    public int Next()
    {
        _id = (_id + 1) % 3;
        return _id;
    }
}
