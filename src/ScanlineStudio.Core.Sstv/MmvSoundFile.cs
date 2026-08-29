namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Pure parsing/resampling for legacy's <c>.MMV</c> sound-file station-ID format (<c>sys.m_MMVID</c>,
/// <c>OutputMMV</c>, <c>Main.cpp:6847-6902</c>) -- a custom raw-PCM format, NOT WAV/MP3. No file I/O
/// here (see `docs/plans/sound-file-id-plan.md`'s "File I/O / DSP layering" -- the caller,
/// <c>ScanlineStudio.Application.SstvSessionService</c>, does the actual <c>File.ReadAllBytes</c> and
/// hands this class the raw bytes). Public (unlike <see cref="CwMorseGenerator"/>/
/// <see cref="FskStationIdEncoder"/>'s internal-static-class shape) because `Application` calls it
/// across the assembly boundary; still stateless/static like those two.
///
/// PCM payload is assumed 16-bit signed, mono, little-endian (inferred from legacy's <c>(short*)</c>
/// casts and single-channel <c>CSSTVMOD::SetRow</c> consumption -- no real <c>.mmv</c> fixture exists
/// in <c>yoniq-old/</c> to confirm directly; flagged as unverified in the plan, not presented as
/// confirmed fact).
/// </summary>
public static class MmvSoundFile
{
    /// <summary><c>ComLib.cpp:68</c>'s <c>SampTable[]</c> -- index-to-Hz for the file header's own
    /// sample-rate byte. Deliberately NOT the same order as this port's own
    /// <c>OptionsWindowViewModel.AvailableSampleRates</c> (which has 14000 where legacy has 6000 at
    /// the same position) -- a faithful <c>.MMV</c> parser must use THIS table, never the port's own
    /// rate list, to decode the header byte.</summary>
    private static readonly int[] SampTable = [11025, 8000, 6000, 12000, 16000, 18000, 22050, 24000, 44100, 48000];

    /// <summary>ui_transition_plan.md step 8 (T2-2): exposes <see cref="SampTable"/> for duration
    /// display in the Options dialog (a validated file's real, ORIGINAL sample rate -- not the port's
    /// TX-time target rate <see cref="Resample"/> converts to) without exposing the table itself.
    /// <paramref name="sampleRateIndex"/> must already be a valid <see cref="Header.SampleRateIndex"/>
    /// from <see cref="ParseHeader"/> -- that method is the only legitimate producer of this index and
    /// already clamps/rejects every out-of-range value, so no further bounds check is done here.</summary>
    public static int GetSourceSampleRateHz(int sampleRateIndex) => SampTable[sampleRateIndex];

    /// <summary>The two facts <see cref="ParseHeader"/> extracts from the raw file header.</summary>
    public readonly record struct Header(int SampleRateIndex, int PayloadOffset);

    /// <summary>Parses the 4-byte (or fewer) file header per <c>OutputMMV</c>'s own magic-byte
    /// sniff (<c>Main.cpp:6857-6866</c>). Returns <see langword="null"/> for any file this port
    /// treats as unplayable -- matching legacy's own "unconfigured sound file -&gt; silent no-op"
    /// behavior (<c>CwIdMode.SoundFile</c>'s own doc comment), never an exception.</summary>
    public static Header? ParseHeader(ReadOnlySpan<byte> fileBytes)
    {
        // Main.cpp:6857: len>=4 required before the header is even read.
        if (fileBytes.Length < 4)
        {
            return null;
        }

        if (fileBytes[0] == 0x55 && fileBytes[1] == 0xAA)
        {
            // Main.cpp:6859-6862: magic present -> Samp=head[2], clamped `>8 -> 0` (legacy's own
            // literal `>8`, not `>9` -- index 9/48000 is unreachable via a magic header in legacy;
            // preserved here as a real, if odd, legacy quirk, not "fixed" to `>9`.
            var sampleRateIndex = fileBytes[2];
            if (sampleRateIndex > 8)
            {
                sampleRateIndex = 0;
            }

            // Main.cpp:6860: `len -= 4` before the payload pointer advances 4 bytes.
            return new Header(sampleRateIndex, PayloadOffset: 4);
        }

        // Main.cpp:6864-6866: no magic -> Samp=head[0], re-seek to file offset 0 (the 4 sniffed
        // bytes ARE audio data, not skipped) -- legacy has NO clamp here at all (`SampTable[10]`
        // indexed directly, undefined behavior in the original C++ for head[0] > 9). This port
        // treats an out-of-range byte as unplayable (a deliberate, documented SAFETY divergence --
        // legacy's own behavior for this case is genuinely unspecifiable, so there is nothing
        // concrete to port), not a fidelity choice.
        if (fileBytes[0] > 9)
        {
            return null;
        }

        return new Header(fileBytes[0], PayloadOffset: 0);
    }

    /// <summary>Converts a raw payload byte span to 16-bit signed little-endian samples, dropping a
    /// trailing odd byte if present (matches legacy's own <c>pos/2</c> truncation on its no-resample
    /// fast path, <c>Main.cpp:6898</c> -- applied here unconditionally since it never discards a
    /// sample that either code path would otherwise use). Manual byte assembly, not
    /// <c>MemoryMarshal.Cast</c>, so the result is correct regardless of host CPU endianness.</summary>
    private static short[] ReadPcm16LittleEndian(ReadOnlySpan<byte> payload)
    {
        var sampleCount = payload.Length / 2;
        var samples = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(payload[i * 2] | (payload[i * 2 + 1] << 8));
        }

        return samples;
    }

    /// <summary>Resamples (if needed) and normalizes <paramref name="payload"/> (the raw audio bytes
    /// starting at <see cref="Header.PayloadOffset"/>) to this port's `float` [-1.0, 1.0] audio
    /// contract at <paramref name="targetSampleRateHz"/> -- always this port's NOMINAL TX
    /// <c>SampleRate</c>, never the TX-offset-corrected rate (matching the precedent already set for
    /// <c>TxOutputBandpassFilter</c>/the TX LPF just above this call site in
    /// <c>AnalogFmSstvEncoder.EncodeAsyncCore</c>). Deliberately resamples straight to this rate
    /// rather than porting legacy's own <c>InitSampType</c>/<c>SampBase</c> quantization ladder
    /// (`ComLib.cpp:106-201`) -- that ladder only ever needs to select among <see cref="SampTable"/>'s
    /// own 10 values because legacy's ENTIRE TX pipeline only ever runs at one of them; this port's
    /// own rate list is not a subset of <see cref="SampTable"/> (it has 14000Hz, which has no
    /// <see cref="SampTable"/> entry at all), so porting the ladder would mean inventing a mapping
    /// legacy itself never needed. Always returns a fresh, caller-owned array (never pooled/shared --
    /// the caller may hold this across a lazy <c>IAsyncEnumerable</c>).</summary>
    public static float[] Resample(ReadOnlySpan<byte> payload, int sourceRateIndex, int targetSampleRateHz)
    {
        var sourceRateHz = SampTable[sourceRateIndex];
        var pcm16 = ReadPcm16LittleEndian(payload);

        if (sourceRateHz == targetSampleRateHz)
        {
            // Main.cpp:6877/6895-6897: rates already match -> raw copy, no resample AND no IIR
            // filter at all (legacy's own "already matches" fast path).
            var direct = new float[pcm16.Length];
            for (var i = 0; i < pcm16.Length; i++)
            {
                direct[i] = pcm16[i] / 32768f;
            }

            return direct;
        }

        // Main.cpp:6887: iir.MakeIIR(2700, SampBase, 4, 0, 0) -- fs is the TARGET rate
        // (fir.h:165's MakeIIR(fc, fs, order, bc, rp) signature confirms this), Butterworth (bc=0).
        var iir = new IirFilter();
        iir.Design(2700.0, targetSampleRateHz, order: 4);

        // Main.cpp:6879-6884: `len = int(double(pos)*SampBase/sfq); len &= 0xfffffffe; len /= 2` --
        // computed in the BYTE domain from the RAW payload length (`pos`, not the odd-truncated
        // short count), matching legacy exactly rather than re-deriving in the sample domain.
        var outputLengthBytes = (int)(payload.Length * (double)targetSampleRateHz / sourceRateHz);
        outputLengthBytes &= ~1;
        var outputSampleCount = outputLengthBytes / 2;

        // Code-review finding (real bug, caught before production): the output-length formula above
        // is computed from the RAW byte length, but the truncating index pick below (`r`) can reach
        // exactly `pcm16.Length` when the target rate exceeds the source rate enough -- an odd-length
        // payload at a large enough upsample ratio indexed straight past the end of `pcm16`, an
        // unhandled IndexOutOfRangeException on the PTT-keyed transmit path. Main.cpp:6869 allocates
        // its OWN source buffer with 2 BYTES of slack (`new BYTE[len+2]`) specifically so this same
        // index can never fault in legacy either -- `sp[r]` there reads defined-but-uninitialized
        // heap for that one trailing slot, not a crash. Zero-padding one extra short is the closest
        // literal analogue available in managed code (legacy's own slack content is unspecifiable
        // uninitialized memory, so there is no "more faithful" value to reproduce).
        var paddedPcm16 = new short[pcm16.Length + 1];
        pcm16.CopyTo(paddedPcm16, 0);

        var output = new float[outputSampleCount];
        for (var i = 0; i < outputSampleCount; i++)
        {
            // Main.cpp:6889: `r = int(i*sfq/SampBase)` -- truncating (not rounding) index pick.
            var r = (int)(i * (double)sourceRateHz / targetSampleRateHz);

            // Main.cpp:6890: `*tp++ = iir.Do(sp[r])`, `sp[r]` picked BEFORE filtering, in double.
            // Legacy re-quantizes the filtered value back to `short` before playback; this port's
            // `float` pipeline skips that intermediate re-quantization (a sub-LSB, documented
            // deviation, not a behavioral one) and divides the double filter output directly.
            var filtered = iir.Process(paddedPcm16[r]);
            output[i] = (float)(filtered / 32768.0);
        }

        return output;
    }
}
