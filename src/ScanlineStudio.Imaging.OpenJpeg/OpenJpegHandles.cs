using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScanlineStudio.Imaging.OpenJpeg;

/// <summary>An OpenJPEG object (codec, stream or image) released by its own destroy function.</summary>
internal sealed unsafe class OpenJpegHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;

    public OpenJpegHandle(nint handle, delegate* unmanaged[Cdecl]<nint, void> destroy)
        : base(ownsHandle: true)
    {
        _destroy = destroy;
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        _destroy(handle);
        return true;
    }
}

/// <summary>A managed byte buffer behind an opj_stream_t, reached from the native callbacks through a
/// GCHandle in the stream's user data. Reads follow opj_read_from_file (return (size_t)-1 at the end);
/// writes, skips and seeks follow a file opened for writing (a skip or seek past the end is allowed and the
/// gap reads as zeros once written over).</summary>
internal sealed unsafe class OpenJpegMemoryStream : IDisposable
{
    /// <summary>Encoder output cap; QSSTV-sized images are kilobytes.</summary>
    private const int MaxOutputBytes = 256 * 1024 * 1024;

    private readonly OpenJpegNative _native;
    private byte[] _data;
    private long _length;
    private long _position;
    private GCHandle _self;

    private OpenJpegMemoryStream(OpenJpegNative native, byte[] data, long length, bool isInput)
    {
        _native = native;
        _data = data;
        _length = length;
        _self = GCHandle.Alloc(this);
        Handle = new OpenJpegHandle(native.opj_stream_create(OpenJpegNative.StreamChunkSize, isInput ? 1 : 0), native.opj_stream_destroy);
        if (Handle.IsInvalid)
        {
            _self.Free();
            throw new InvalidOperationException("opj_stream_create failed.");
        }

        nint s = Handle.DangerousGetHandle();
        native.opj_stream_set_user_data(s, GCHandle.ToIntPtr(_self), 0);
        if (isInput)
        {
            native.opj_stream_set_user_data_length(s, (ulong)length);
            native.opj_stream_set_read_function(s, (nint)(delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint>)&Read);
        }
        else
        {
            native.opj_stream_set_write_function(s, (nint)(delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint>)&Write);
        }

        native.opj_stream_set_skip_function(s, (nint)(delegate* unmanaged[Cdecl]<long, void*, long>)&Skip);
        native.opj_stream_set_seek_function(s, (nint)(delegate* unmanaged[Cdecl]<long, void*, int>)&Seek);
    }

    public OpenJpegHandle Handle { get; }

    public static OpenJpegMemoryStream ForReading(OpenJpegNative native, ReadOnlySpan<byte> data) =>
        new(native, data.ToArray(), data.Length, isInput: true);

    public static OpenJpegMemoryStream ForWriting(OpenJpegNative native) =>
        new(native, new byte[64 * 1024], 0, isInput: false);

    /// <summary>The bytes written so far.</summary>
    public byte[] ToArray() => _data.AsSpan(0, (int)_length).ToArray();

    public void Dispose()
    {
        // The stream goes first: its destroy may still call back while flushing.
        Handle.Dispose();
        if (_self.IsAllocated)
            _self.Free();
    }

    private static OpenJpegMemoryStream? From(void* user) =>
        GCHandle.FromIntPtr((nint)user).Target as OpenJpegMemoryStream;

    // Every callback body is guarded: an exception unwinding into OpenJPEG's C frames would kill the process.
#pragma warning disable CA1031
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(void* buffer, nuint count, void* user)
    {
        try
        {
            OpenJpegMemoryStream? s = From(user);
            if (s is null || s._position >= s._length)
                return nuint.MaxValue;
            int n = (int)Math.Min((ulong)(s._length - s._position), (ulong)count);
            s._data.AsSpan((int)s._position, n).CopyTo(new Span<byte>(buffer, n));
            s._position += n;
            return (nuint)n;
        }
        catch (Exception)
        {
            return nuint.MaxValue;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Write(void* buffer, nuint count, void* user)
    {
        try
        {
            OpenJpegMemoryStream? s = From(user);
            if (s is null || (ulong)s._position + count > MaxOutputBytes)
                return nuint.MaxValue;
            int end = (int)(s._position + (long)count);
            if (end > s._data.Length)
                Array.Resize(ref s._data, Math.Max(end, Math.Min(MaxOutputBytes, s._data.Length * 2)));
            new ReadOnlySpan<byte>(buffer, (int)count).CopyTo(s._data.AsSpan((int)s._position));
            s._position = end;
            s._length = Math.Max(s._length, end);
            return count;
        }
        catch (Exception)
        {
            return nuint.MaxValue;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Skip(long count, void* user)
    {
        try
        {
            OpenJpegMemoryStream? s = From(user);
            if (s is null || s._position + count < 0 || s._position + count > MaxOutputBytes)
                return -1;
            s._position += count;
            return count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Seek(long offset, void* user)
    {
        try
        {
            OpenJpegMemoryStream? s = From(user);
            if (s is null || offset < 0 || offset > MaxOutputBytes)
                return 0;
            s._position = offset;
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }
#pragma warning restore CA1031
}

/// <summary>Collects OpenJPEG error and warning messages for one codec, for exception text.</summary>
internal sealed unsafe class OpenJpegMessages : IDisposable
{
    private const int MaxMessages = 16;
    private readonly List<string> _errors = [];
    private GCHandle _self;

    public OpenJpegMessages() => _self = GCHandle.Alloc(this);

    /// <summary>Routes <paramref name="codec"/>'s messages here. Create this object before the codec, so that
    /// disposal destroys the codec first and only then frees the handle the codec's callbacks use.</summary>
    public void Attach(OpenJpegNative native, OpenJpegHandle codec)
    {
        nint c = codec.DangerousGetHandle();
        nint user = GCHandle.ToIntPtr(_self);
        native.opj_set_error_handler(c, (nint)(delegate* unmanaged[Cdecl]<byte*, void*, void>)&OnError, user);
        native.opj_set_warning_handler(c, (nint)(delegate* unmanaged[Cdecl]<byte*, void*, void>)&OnWarning, user);
    }

    public IReadOnlyList<string> Errors => _errors;

    public string Describe(string step) =>
        _errors.Count == 0 ? $"OpenJPEG {step} failed." : $"OpenJPEG {step} failed: {string.Join(" ", _errors)}";

    public void Dispose()
    {
        if (_self.IsAllocated)
            _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(byte* message, void* user)
    {
#pragma warning disable CA1031 // Must not unwind into native code.
        try
        {
            if (GCHandle.FromIntPtr((nint)user).Target is OpenJpegMessages m && m._errors.Count < MaxMessages)
                m._errors.Add((Marshal.PtrToStringUTF8((nint)message) ?? string.Empty).Trim());
        }
        catch (Exception)
        {
            // The message is lost; the failing call still reports failure.
        }
#pragma warning restore CA1031
    }

    // Warnings are not errors; OpenJPEG would otherwise print them to stderr.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWarning(byte* message, void* user)
    {
    }
}
