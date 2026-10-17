using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The result of <see cref="RsFileDecoder.TryDecode"/>.</summary>
/// <param name="QsstvFileName">The name QSSTV saves the decoded file under, without its directory:
/// <c>completeBaseName</c> of the transported name, ".", and the 3-byte coded extension (reedsolomoncoder.cpp:159-171).</param>
/// <param name="File">The decoded file bytes.</param>
/// <param name="CorrectedSymbols">QSSTV's <c>sumOfFailures</c>: symbols corrected over all code words, erasures included.</param>
internal sealed record RsDecodeResult(string QsstvFileName, byte[] File, int CorrectedSymbols);

/// <summary>Port of QSSTV's <c>reedSolomonCoder::decode</c>, <c>decode_and_write</c> and the erasure labelling
/// (utils/reedsolomoncoder.cpp:52-178,208-245; Johan Maes ON4QZ / PA0MBO, Karn decoder rs.cpp): undoes the
/// interleave of <see cref="RsFileEncoder"/>, marks every byte of a missing MOT segment as an erasure, decodes each
/// RS(255,k) word and strips the 7-byte header.</summary>
/// <remarks>
/// <para><b>Interpretation-side fix (RS1 signed-char size bug).</b> QSSTV reads the surplus byte as <c>(int)tr_buf[0]</c>
/// (:158), a signed <c>char</c> on x86: a surplus of 128 or more makes the file size too large and the file is
/// saved with trailing zeros (B1_q4_high_long_rs1: 300 bytes saved as 441). aarch64's unsigned <c>char</c> gives the
/// right size. We read it unsigned; <c>qsstvSignedSurplus</c> reproduces x86 QSSTV for the golden tests only.</para>
/// <para><b>Defined values for QSSTV undefined behaviour.</b> QSSTV allocates the erasure map for
/// <c>segmentLength·(totalSegments+1)</c> bytes but initialises only <c>segmentLength·totalSegments</c>, labels missing
/// segments without a bound and reads <c>255·bep_size</c> entries (:109-137). Here every entry outside the labelled
/// missing segments, and every position beyond the allocation, is "not erased".</para>
/// <para><b>QSSTV quirk kept:</b> when the file length mod k exceeds k−7, the surplus byte cannot describe the
/// padding and QSSTV saves the file with trailing zeros (the size is <c>bep_size·k − surplus</c>, clipped to the data).</para>
/// </remarks>
internal static class RsFileDecoder
{
    private const int BlockSize = KarnReedSolomon.BlockSize;

    private static readonly KarnReedSolomon[] Coders =
        [new(RsFileEncoder.DataSize(DrmReedSolomon.Rs1)), new(RsFileEncoder.DataSize(DrmReedSolomon.Rs2)),
         new(RsFileEncoder.DataSize(DrmReedSolomon.Rs3)), new(RsFileEncoder.DataSize(DrmReedSolomon.Rs4))];

    /// <summary>The RS level of a lower-cased suffix: 1..4 for "rs1".."rs4", else 0 (reedsolomoncoder.cpp:63-70).</summary>
    public static int Level(string extension) => extension switch
    {
        "rs1" => 1,
        "rs2" => 2,
        "rs3" => 3,
        "rs4" => 4,
        _ => 0,
    };

    /// <summary><c>reedSolomonCoder::decode</c>. <paramref name="body"/> is the reassembled MOT body (missing segments
    /// as <paramref name="erasures"/>[1] zero bytes each); <paramref name="transportName"/> is the MOT ContentName;
    /// <paramref name="erasures"/> is QSSTV's <c>erasureList</c>: total segments, default segment size, then the
    /// missing segment indices (sourcedecoder.cpp:465-480).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, string transportName, string extension, IReadOnlyList<int> erasures,
        bool qsstvSignedSurplus, out RsDecodeResult? result)
    {
        ArgumentNullException.ThrowIfNull(transportName);
        ArgumentNullException.ThrowIfNull(erasures);
        result = null;
        int level = Level(extension);
        if (level == 0)
            return false;
        KarnReedSolomon coder = Coders[level - 1];
        int dsize = coder.DataSize;

        int got = body.Length;
        int bep = got / BlockSize;
        if (got % BlockSize != 0)
            bep++;
        if (bep == 0 || erasures.Count < 2)
            return false;

        int rc = bep * BlockSize;
        var tr = new byte[rc];
        body.CopyTo(tr);

        // Missing segment s covers body bytes [s·segLen, (s+1)·segLen); positions past the map are not erased (:104-128).
        int totalSegments = erasures[0];
        int segmentLength = erasures[1];
        long mapLength = (long)segmentLength * (totalSegments + 1);
        var missing = new HashSet<int>();
        for (int i = 2; i < erasures.Count; i++)
            missing.Add(erasures[i]);

        var ec = new byte[rc];
        RsFileEncoder.Distribute(tr, ec, bep, BlockSize, reverse: true);

        // decode_and_write (:208-245): word i holds transmitted bytes p = j·bep + i, j = 0..254.
        var decoded = new byte[bep * dsize];
        Span<int> erasPos = stackalloc int[BlockSize];
        int corrected = 0;
        for (int i = 0; i < bep; i++)
        {
            int nrErasures = 0;
            if (segmentLength > 0 && missing.Count > 0)
            {
                for (int j = 0; j < BlockSize; j++)
                {
                    long p = ((long)j * bep) + i;
                    if (p < mapLength && missing.Contains((int)(p / segmentLength)))
                    {
                        erasPos[nrErasures++] = j;
                        if (nrErasures > BlockSize - dsize)
                            return false;
                    }
                }
            }

            int failure = coder.DecodeErasures(ec.AsSpan(i * BlockSize, BlockSize), erasPos, nrErasures);
            if (failure == -1)
                return false;
            if (failure > 0)
                corrected += failure;
            ec.AsSpan(i * BlockSize, dsize).CopyTo(decoded.AsSpan(i * dsize));
        }

        if (bep != decoded[1] + (decoded[2] * 256))
            return false;

        int surplus = qsstvSignedSurplus ? (sbyte)decoded[0] : decoded[0];
        int codedFileSize = (bep * dsize) - surplus;

        // strncpy(coded_file_ext, tr_buf+3, 3): up to the first NUL, then QString::append(const char*) = fromUtf8 (:159-166).
        string ext = QtUtf8.DecodeNulTerminated(decoded.AsSpan(3, 3));
        string name = CompleteBaseName(transportName) + "." + ext;

        // tr_buf.right(count-7).left(coded_file_size), with QByteArray's clamping: a negative size gives an empty file.
        int available = decoded.Length - RsFileEncoder.HeaderSize;
        int length = codedFileSize < 0 ? 0 : Math.Min(codedFileSize, available);
        result = new RsDecodeResult(name, decoded.AsSpan(RsFileEncoder.HeaderSize, length).ToArray(), corrected);
        return true;
    }

    /// <summary>False when <see cref="TryDecode"/> is certain to fail on erasures alone: some code word has more erased
    /// symbols than the parity. Counts every word without decoding, in O(missing bytes), using <see cref="TryDecode"/>'s
    /// own index math. True does not mean the decode succeeds (bit errors can still fail it).</summary>
    /// <param name="bodyLength">The length <see cref="TryDecode"/>'s body would have.</param>
    /// <param name="level">The RS level, 1..4.</param>
    /// <param name="erasures">As for <see cref="TryDecode"/>.</param>
    public static bool ErasuresDecodable(long bodyLength, int level, IReadOnlyList<int> erasures)
    {
        ArgumentNullException.ThrowIfNull(erasures);
        if (level is < 1 or > 4)
            return false;
        int parity = BlockSize - Coders[level - 1].DataSize;

        long bep = bodyLength / BlockSize;
        if (bodyLength % BlockSize != 0)
            bep++;
        if (bep == 0 || erasures.Count < 2)
            return false;

        int segmentLength = erasures[1];
        if (segmentLength <= 0 || erasures.Count == 2)
            return true;

        // Word i holds positions p = j·bep + i (j < 255), so the counted range is p < 255·bep, clipped to the map.
        long mapLength = (long)segmentLength * (erasures[0] + 1);
        long limit = Math.Min(mapLength, BlockSize * bep);
        var counts = new int[bep];
        var seen = new HashSet<int>();
        for (int k = 2; k < erasures.Count; k++)
        {
            int s = erasures[k];
            if (!seen.Add(s))
                continue;
            long start = Math.Max(0L, (long)s * segmentLength);
            long end = Math.Min(limit, ((long)s + 1) * segmentLength);
            for (long p = start; p < end; p++)
            {
                if (++counts[p % bep] > parity)
                    return false;
            }
        }

        return true;
    }

    /// <summary><c>QFileInfo(dir + "/" + name).completeBaseName()</c>: the last '/'-separated component, up to its last '.'.</summary>
    public static string CompleteBaseName(string name)
    {
        string file = QsstvPath.FileName(name);
        int dot = file.LastIndexOf('.');
        return dot < 0 ? file : file[..dot];
    }
}

/// <summary>QFileInfo's name splitting on the paths QSSTV builds as <c>rxDRMImagesPath + "/" + name</c> (Unix '/' only).</summary>
internal static class QsstvPath
{
    /// <summary><c>QFileInfo::fileName()</c>: the text after the last '/'.</summary>
    public static string FileName(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    /// <summary><c>QFileInfo::suffix()</c>: the text after the last '.' of <see cref="FileName"/>, or empty.</summary>
    public static string Suffix(string name)
    {
        string file = FileName(name);
        int dot = file.LastIndexOf('.');
        return dot < 0 ? string.Empty : file[(dot + 1)..];
    }
}
