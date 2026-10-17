namespace ScanlineStudio.Abstractions.Drm;

/// <summary>A file the DRM source decoder completed: the point where QSSTV writes it to its RX folder and calls
/// <c>saveImage</c> (sourcedecoder.cpp:694-706,507-569). Core does not write files; the consumer stores
/// <see cref="Data"/> under <see cref="FileName"/> (made unique with Core.Drm's <c>DrmReceivedFileNames.MakeUnique</c>).</summary>
/// <param name="FileName">Safe file name: <see cref="QsstvFileName"/> through Core.Drm's <c>DrmReceivedFileNames.Sanitize</c>.</param>
/// <param name="QsstvFileName">The name QSSTV would save under, below its RX folder, unsanitized. Non-RS: the MOT
/// ContentName. RS: its base name plus the 3-byte coded extension (reedsolomoncoder.cpp:163-171).</param>
/// <param name="TransportName">The MOT ContentName as received (Latin-1, cut at the first NUL, sourcedecoder.cpp:395).</param>
/// <param name="Data">The file bytes.</param>
/// <param name="TransportId">MOT transport ID; 0xFFFF when the data groups carry none.</param>
/// <param name="Callsign">The FAC callsign recorded while body segments arrived; empty if none was complete.</param>
/// <param name="ModeCode">QSSTV's compact mode code: mode·10000 + occupancy·1000 + protection·100 + QAM·10 + interleave
/// (sourcedecoder.cpp:836-841); QAM 0 = 4, 1 = 16, 2 = 64.</param>
/// <param name="RobustnessMode">0 = A, 1 = B, 2 = E, when the transport block opened.</param>
/// <param name="SpectrumOccupancy">FAC spectrum occupancy (0 = 2.3 kHz, 1 = 2.5 kHz).</param>
/// <param name="MscMode">FAC <c>msc_mode_new</c>: 0 = 64-QAM, 1 = 16-QAM, 3 = 4-QAM.</param>
/// <param name="ProtectionB">Multiplex <c>PL_PartB</c>: 0 = high, 1 = low.</param>
/// <param name="InterleaverDepth">0 = long, 1 = short.</param>
/// <param name="ContentType">MOT header ContentType (6 bits).</param>
/// <param name="ContentSubtype">MOT header ContentSubType (9 bits).</param>
/// <param name="BodySize">MOT header BodySize (28 bits); the RS-coded size for RS files.</param>
/// <param name="TotalSegments">Body segments of the transport block.</param>
/// <param name="SegmentsReceived">Body segments received when the file completed.</param>
/// <param name="ReedSolomonLevel">0 = not RS coded, 1..4 = rs1..rs4.</param>
/// <param name="RsCorrectedSymbols">RS symbols corrected (erasures included); 0 for non-RS files.</param>
/// <param name="IsQsstvTextMode">QSSTV would show it as text, not as an image: the suffix is "txt" or "chat" and the
/// content is not a recognised image (sourcedecoder.cpp:607-619). See Core.Drm's <c>DrmSourceDecoder</c>.</param>
/// <param name="IsQsstvHybridName">The name starts with ".de" or "de_": a QSSTV hybrid (FTP) stub, which QSSTV saves but
/// does not display when hybrid RX is off (sourcedecoder.cpp:525-563). Hybrid is not supported.</param>
public sealed record DrmReceivedFile(
    string FileName,
    string QsstvFileName,
    string TransportName,
    ReadOnlyMemory<byte> Data,
    int TransportId,
    string Callsign,
    int ModeCode,
    int RobustnessMode,
    int SpectrumOccupancy,
    int MscMode,
    int ProtectionB,
    int InterleaverDepth,
    int ContentType,
    int ContentSubtype,
    long BodySize,
    int TotalSegments,
    int SegmentsReceived,
    int ReedSolomonLevel,
    int RsCorrectedSymbols,
    bool IsQsstvTextMode,
    bool IsQsstvHybridName);
