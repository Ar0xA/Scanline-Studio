using System.Runtime.InteropServices;

namespace ScanlineStudio.Imaging.OpenJpeg;

/// <summary>The libopenjp2 2.5.4 entry points this codec uses, resolved by name from one explicitly loaded
/// file. Struct layouts are not mirrored in full: the parameter structs are opaque native buffers and only
/// the fields QSSTV touches are written, at offsets taken from the 2.5.4 headers (identical on the three
/// shipped 64-bit targets: the structs hold only int, float, char and pointer members). The load fails,
/// rather than risking a wrong write, if the version string differs or the defaults read back wrong.</summary>
internal sealed unsafe class OpenJpegNative
{
    public const string RequiredVersion = "2.5.4";

    // OPJ_CODEC_FORMAT, OPJ_COLOR_SPACE (openjpeg.h:316-338).
    public const int CodecJ2k = 0;
    public const int CodecJp2 = 2;
    public const int ColorSpaceSrgb = 1;
    public const int ColorSpaceSycc = 3;
    public const int ColorSpaceEycc = 4;

    /// <summary>OPJ_J2K_STREAM_CHUNK_SIZE, the buffer opj_stream_create_default_file_stream uses.</summary>
    public const nuint StreamChunkSize = 0x100000;

    // sizeof(opj_cparameters_t) = 18720 and sizeof(opj_dparameters_t) = 8252 on 2.5.4 x64; allocated with headroom.
    public const int EncoderParametersBufferSize = 32 * 1024;
    public const int DecoderParametersBufferSize = 16 * 1024;

    // opj_cparameters_t field offsets (2.5.4, 64-bit).
    public const int CpDistoAllocOffset = 20;
    public const int TcpNumLayersOffset = 4796;
    public const int TcpRatesOffset = 4800;
    public const int NumResolutionOffset = 5600;
    public const int CblockWInitOffset = 5604;
    public const int CblockHInitOffset = 5608;
    public const int SubsamplingDxOffset = 18196;
    public const int SubsamplingDyOffset = 18200;
    public const int TcpMctOffset = 18698;

    // opj_image_t and opj_image_comp_t field offsets (2.5.4, 64-bit).
    public const int ImageX0Offset = 0;
    public const int ImageY0Offset = 4;
    public const int ImageX1Offset = 8;
    public const int ImageY1Offset = 12;
    public const int ImageNumCompsOffset = 16;
    public const int ImageColorSpaceOffset = 20;
    public const int ImageCompsOffset = 24;
    public const int CompSize = 64;
    public const int CompDxOffset = 0;
    public const int CompDyOffset = 4;
    public const int CompWOffset = 8;
    public const int CompHOffset = 12;
    public const int CompPrecOffset = 24;
    public const int CompSgndOffset = 32;
    public const int CompDataOffset = 48;

    // opj_codestream_info_v2_t: tw, th (number of tiles across and down).
    public const int CstrInfoTwOffset = 16;
    public const int CstrInfoThOffset = 20;

    private OpenJpegNative(nint library, string version)
    {
        Library = library;
        Version = version;
        opj_version = (delegate* unmanaged[Cdecl]<byte*>)Export(library, nameof(opj_version));
        opj_create_compress = (delegate* unmanaged[Cdecl]<int, nint>)Export(library, nameof(opj_create_compress));
        opj_create_decompress = (delegate* unmanaged[Cdecl]<int, nint>)Export(library, nameof(opj_create_decompress));
        opj_destroy_codec = (delegate* unmanaged[Cdecl]<nint, void>)Export(library, nameof(opj_destroy_codec));
        opj_set_default_encoder_parameters = (delegate* unmanaged[Cdecl]<void*, void>)Export(library, nameof(opj_set_default_encoder_parameters));
        opj_set_default_decoder_parameters = (delegate* unmanaged[Cdecl]<void*, void>)Export(library, nameof(opj_set_default_decoder_parameters));
        opj_setup_encoder = (delegate* unmanaged[Cdecl]<nint, void*, nint, int>)Export(library, nameof(opj_setup_encoder));
        opj_setup_decoder = (delegate* unmanaged[Cdecl]<nint, void*, int>)Export(library, nameof(opj_setup_decoder));
        opj_image_create = (delegate* unmanaged[Cdecl]<uint, ImageComponentParameters*, int, nint>)Export(library, nameof(opj_image_create));
        opj_image_destroy = (delegate* unmanaged[Cdecl]<nint, void>)Export(library, nameof(opj_image_destroy));
        opj_start_compress = (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)Export(library, nameof(opj_start_compress));
        opj_encode = (delegate* unmanaged[Cdecl]<nint, nint, int>)Export(library, nameof(opj_encode));
        opj_end_compress = (delegate* unmanaged[Cdecl]<nint, nint, int>)Export(library, nameof(opj_end_compress));
        opj_read_header = (delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)Export(library, nameof(opj_read_header));
        opj_decode = (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)Export(library, nameof(opj_decode));
        opj_end_decompress = (delegate* unmanaged[Cdecl]<nint, nint, int>)Export(library, nameof(opj_end_decompress));
        opj_get_cstr_info = (delegate* unmanaged[Cdecl]<nint, nint>)Export(library, nameof(opj_get_cstr_info));
        opj_destroy_cstr_info = (delegate* unmanaged[Cdecl]<nint*, void>)Export(library, nameof(opj_destroy_cstr_info));
        opj_stream_create = (delegate* unmanaged[Cdecl]<nuint, int, nint>)Export(library, nameof(opj_stream_create));
        opj_stream_destroy = (delegate* unmanaged[Cdecl]<nint, void>)Export(library, nameof(opj_stream_destroy));
        opj_stream_set_read_function = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(library, nameof(opj_stream_set_read_function));
        opj_stream_set_write_function = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(library, nameof(opj_stream_set_write_function));
        opj_stream_set_skip_function = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(library, nameof(opj_stream_set_skip_function));
        opj_stream_set_seek_function = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(library, nameof(opj_stream_set_seek_function));
        opj_stream_set_user_data = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)Export(library, nameof(opj_stream_set_user_data));
        opj_stream_set_user_data_length = (delegate* unmanaged[Cdecl]<nint, ulong, void>)Export(library, nameof(opj_stream_set_user_data_length));
        opj_set_error_handler = (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)Export(library, nameof(opj_set_error_handler));
        opj_set_warning_handler = (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)Export(library, nameof(opj_set_warning_handler));
    }

    public nint Library { get; }

    public string Version { get; }

#pragma warning disable SA1300, IDE1006, CA1707 // Named as the C exports they hold.
    public readonly delegate* unmanaged[Cdecl]<byte*> opj_version;
    public readonly delegate* unmanaged[Cdecl]<int, nint> opj_create_compress;
    public readonly delegate* unmanaged[Cdecl]<int, nint> opj_create_decompress;
    public readonly delegate* unmanaged[Cdecl]<nint, void> opj_destroy_codec;
    public readonly delegate* unmanaged[Cdecl]<void*, void> opj_set_default_encoder_parameters;
    public readonly delegate* unmanaged[Cdecl]<void*, void> opj_set_default_decoder_parameters;
    public readonly delegate* unmanaged[Cdecl]<nint, void*, nint, int> opj_setup_encoder;
    public readonly delegate* unmanaged[Cdecl]<nint, void*, int> opj_setup_decoder;
    public readonly delegate* unmanaged[Cdecl]<uint, ImageComponentParameters*, int, nint> opj_image_create;
    public readonly delegate* unmanaged[Cdecl]<nint, void> opj_image_destroy;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int> opj_start_compress;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, int> opj_encode;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, int> opj_end_compress;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint*, int> opj_read_header;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int> opj_decode;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, int> opj_end_decompress;
    public readonly delegate* unmanaged[Cdecl]<nint, nint> opj_get_cstr_info;
    public readonly delegate* unmanaged[Cdecl]<nint*, void> opj_destroy_cstr_info;
    public readonly delegate* unmanaged[Cdecl]<nuint, int, nint> opj_stream_create;
    public readonly delegate* unmanaged[Cdecl]<nint, void> opj_stream_destroy;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> opj_stream_set_read_function;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> opj_stream_set_write_function;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> opj_stream_set_skip_function;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, void> opj_stream_set_seek_function;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, void> opj_stream_set_user_data;
    public readonly delegate* unmanaged[Cdecl]<nint, ulong, void> opj_stream_set_user_data_length;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int> opj_set_error_handler;
    public readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int> opj_set_warning_handler;
#pragma warning restore SA1300, IDE1006, CA1707

    /// <summary>The bundled library's file name for the running OS (see the project file).</summary>
    public static string PlatformFileName =>
        OperatingSystem.IsWindows() ? "openjp2.dll"
        : OperatingSystem.IsMacOS() ? "libopenjp2.7.dylib"
        : "libopenjp2.so.7";

    /// <summary>Loads <paramref name="path"/> and checks it. Never throws; on failure the library is
    /// unloaded and <paramref name="reason"/> says why.</summary>
    public static OpenJpegNative? TryLoad(string path, out string? reason)
    {
        if (!File.Exists(path))
        {
            reason = $"OpenJPEG library not found at '{path}'.";
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out nint library))
        {
            reason = $"OpenJPEG library at '{path}' could not be loaded (wrong architecture, or a missing dependency such as the Visual C++ runtime).";
            return null;
        }

        try
        {
            var versionFn = (delegate* unmanaged[Cdecl]<byte*>)Export(library, nameof(opj_version));
            string version = Marshal.PtrToStringAnsi((nint)versionFn()) ?? string.Empty;
            if (version != RequiredVersion)
            {
                NativeLibrary.Free(library);
                reason = $"OpenJPEG at '{path}' is version '{version}'; this build needs {RequiredVersion}.";
                return null;
            }

            var native = new OpenJpegNative(library, version);
            if (!native.DefaultsMatchLayout())
            {
                NativeLibrary.Free(library);
                reason = $"OpenJPEG at '{path}' does not have the expected parameter layout.";
                return null;
            }

            reason = null;
            return native;
        }
        catch (EntryPointNotFoundException ex)
        {
            NativeLibrary.Free(library);
            reason = $"OpenJPEG at '{path}' lacks an expected export: {ex.Message}";
            return null;
        }
    }

    private static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out nint address)
            ? address
            : throw new EntryPointNotFoundException(name);

    /// <summary>opj_set_default_encoder_parameters' known defaults (opj_compress docs, j2k.c) read back at
    /// the offsets this binding writes to.</summary>
    private bool DefaultsMatchLayout()
    {
        byte* p = (byte*)NativeMemory.AllocZeroed(EncoderParametersBufferSize);
        try
        {
            opj_set_default_encoder_parameters(p);
            return *(int*)(p + NumResolutionOffset) == 6
                && *(int*)(p + CblockWInitOffset) == 64
                && *(int*)(p + CblockHInitOffset) == 64
                && *(int*)(p + SubsamplingDxOffset) == 1
                && *(int*)(p + SubsamplingDyOffset) == 1
                && *(int*)(p + TcpNumLayersOffset) == 0
                && *(int*)(p + CpDistoAllocOffset) == 0
                && *(float*)(p + TcpRatesOffset) == 0f
                && p[TcpMctOffset] == 0;
        }
        finally
        {
            NativeMemory.Free(p);
        }
    }

    /// <summary>opj_image_cmptparm_t (openjpeg.h:740-760), nine OPJ_UINT32.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImageComponentParameters
    {
        public uint Dx;
        public uint Dy;
        public uint W;
        public uint H;
        public uint X0;
        public uint Y0;
        public uint Prec;
        public uint Bpp;
        public uint Sgnd;
    }
}
