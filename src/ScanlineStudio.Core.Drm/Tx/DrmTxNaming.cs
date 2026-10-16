using System.Globalization;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>A TX file name with QFileInfo's splitting rules (Qt 5.15).</summary>
internal sealed record DrmTxFileName(string FileName)
{
    /// <summary>QFileInfo::baseName: up to the first '.'.</summary>
    public string BaseName => FileName.IndexOf('.', StringComparison.Ordinal) is var i and >= 0 ? FileName[..i] : FileName;

    /// <summary>QFileInfo::completeBaseName: up to the last '.'.</summary>
    public string CompleteBaseName => FileName.LastIndexOf('.') is var i and >= 0 ? FileName[..i] : FileName;

    /// <summary>QFileInfo::suffix: after the last '.', empty without one.</summary>
    public string Suffix => FileName.LastIndexOf('.') is var i and >= 0 ? FileName[(i + 1)..] : string.Empty;

    /// <summary>QFileInfo::fileName: the part after the last directory separator of the running OS,
    /// so '\' also separates on Windows.</summary>
    public static string StripPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetFileName(path);
    }
}

/// <summary>QSSTV's TX naming (drmtx.cpp:45-78,109-125,339-344). Per-transmitter state: the UTC
/// time stamp is taken once and reused until <see cref="ForgetTxFileName"/>, which QSSTV calls when
/// a template is applied. Hybrid ("de_") naming is not ported.</summary>
public sealed class DrmTxNaming
{
    private readonly TimeProvider _timeProvider;
    private string _stamp = string.Empty;

    public DrmTxNaming(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>drmTx::forgetTxFileName (drmtx.cpp:45-48).</summary>
    public void ForgetTxFileName() => _stamp = string.Empty;

    /// <summary>drmTx::getTxFileName plus initDRMImage's ".jp2" rename (drmtx.cpp:50-78,114).
    /// <paramref name="binary"/> = false is the image path: the name becomes <c>completeBaseName + ".jp2"</c>.</summary>
    internal DrmTxFileName GetTxFileName(string fileName, bool binary)
    {
        if (_stamp.Length == 0)
            _stamp = _timeProvider.GetUtcNow().ToString(StampFormat, CultureInfo.InvariantCulture);

        return Build(_stamp, fileName, binary);
    }

    /// <summary>The name <see cref="GetTxFileName"/> would give, with a same-length placeholder stamp: the MOT
    /// header, and so the frame count, depend on the name's length only. Reads and changes no state.</summary>
    internal static DrmTxFileName PlaceholderTxFileName(string fileName, bool binary) =>
        Build(new string('0', StampFormat.Length), fileName, binary);

    private const string StampFormat = "yyyyMMddHHmmss";

    private static DrmTxFileName Build(string stamp, string fileName, bool binary)
    {
        var name = new DrmTxFileName(stamp + "-" + DrmTxFileName.StripPath(fileName));
        return binary ? name : new DrmTxFileName(name.CompleteBaseName + ".jp2");
    }
}

/// <summary>Builds the on-air MOT object name exactly as QSSTV does.</summary>
internal static class DrmMotName
{
    /// <summary>The BSR object name: drmTx::initDRMBSR passes "bsr"/"bin" with no stamp (drmtx.cpp:295).</summary>
    public static ReadOnlySpan<byte> Bsr => "bsr.bin"u8;

    /// <summary>AddArray stores <c>name.toLatin1()</c> into a QString, which Qt decodes as UTF-8 up to
    /// the first NUL (MOTSlideShow.cpp:130). AddNextPicture then sends
    /// <c>(name + "." + format).toLatin1().data()</c> (MOTSlideShow.cpp:100-101). So a Latin-1 'é'
    /// alone goes out as '?', while the pair "Ã©" goes out as 'é'.</summary>
    public static byte[] Build(string name, string format)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(format);
        string stored = QtUtf8.DecodeNulTerminated(DrmLatin1.Encode(name));
        return DrmLatin1.EncodeNulTerminated(stored + "." + format);
    }
}
