namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Port of <c>CMOTSlideShowEncoder</c> for QSSTV's single object per transmission
/// (drmtx/common/datadecoding/MOTSlideShow.cpp:43-120), including the end of transmission: after
/// the last scheduled group, <see cref="ExtraBlocks"/> more groups are sent from the start of the
/// schedule, then the transmitter stops. Not thread-safe.</summary>
internal sealed class MotSlideShowEncoder
{
    /// <summary>extraBlocks (MOTSlideShow.cpp:85).</summary>
    public const int ExtraBlocks = 5;

    private readonly MotEncoder _mot;
    private readonly byte[] _name;
    private readonly byte[] _body;
    private readonly IReadOnlyList<int> _fixBlockList;
    private int _bytesToBeUsed;
    private bool _allDataSent;
    private int _extraBlocks;

    public MotSlideShowEncoder(MotEncoder mot, ReadOnlySpan<byte> motName, ReadOnlySpan<byte> body, IReadOnlyList<int> fixBlockList)
    {
        ArgumentNullException.ThrowIfNull(mot);
        ArgumentNullException.ThrowIfNull(fixBlockList);
        _mot = mot;
        _name = motName.ToArray();
        _body = body.ToArray();
        _fixBlockList = fixBlockList;
    }

    /// <summary>QSSTV's global <c>stopDRM</c>: set by the group fetch after the extra blocks.</summary>
    public bool StopRequested { get; private set; }

    /// <summary>CMOTSlideShowEncoder::Init (MOTSlideShow.cpp:77-89).</summary>
    public void Init(int numDecodedBitsMsc)
    {
        _bytesToBeUsed = numDecodedBitsMsc / 8;
        _mot.Reset();
        AddNextPicture();
        _allDataSent = false;
        _extraBlocks = ExtraBlocks;
        _mot.PrepareSegmentList(_fixBlockList, 1);
    }

    /// <summary>CMOTSlideShowEncoder::GetDataUnit (MOTSlideShow.cpp:43-61). The stop check runs before the
    /// fetch, so the group fetched with the stop is still built (it is never completely sent).</summary>
    public byte[] GetDataUnit()
    {
        if (_allDataSent && _extraBlocks-- <= 0)
            StopRequested = true;

        if (_mot.GetDataGroup(out byte[] group))
        {
            _allDataSent = true;
            // Re-sets the same object: a "bsr.bin" advances its transport ID here.
            AddNextPicture();
        }

        return group;
    }

    /// <summary>CMOTSlideShowEncoder::AddNextPicture (MOTSlideShow.cpp:91-120).</summary>
    private void AddNextPicture() => _mot.SetMotObject(_name, _body, _bytesToBeUsed);
}
