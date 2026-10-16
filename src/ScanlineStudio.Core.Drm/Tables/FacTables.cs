namespace ScanlineStudio.Core.Drm.Tables;

/// <summary>The FAC constants HamDRM uses. QSSTV's TableFAC.h/.cpp otherwise holds broadcast-DRM
/// string tables (languages, programme types, countries, CIRAF zones) and iTableNumOfServices, which
/// only the broadcast FAC receiver (FAC.cpp:473-477) reads; QSSTV's own RX decodes FAC in
/// drmrx/channeldecode.cpp instead, so none of those are ported.</summary>
internal static class FacTables
{
    /// <summary>NUM_FAC_BITS_PER_BLOCK, including the 8 CRC bits (TableFAC.h:42).</summary>
    public const int NumFacBitsPerBlock = 48;

    /// <summary>The FAC CRC degree (FAC.cpp:177, CRCObject.Reset(8)).</summary>
    public const int CrcDegree = 8;

    /// <summary>Maximum FAC label length in characters, 3 per frame (FAC.cpp:153-158).</summary>
    public const int MaxLabelLength = 9;
}
