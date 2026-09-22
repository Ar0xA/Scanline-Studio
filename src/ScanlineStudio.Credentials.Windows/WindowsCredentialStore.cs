using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Settings;

namespace ScanlineStudio.Credentials.Windows;

/// <summary><see cref="ICredentialStore"/> over the Windows Credential Manager (advapi32 <c>Cred*</c>).
/// Generic credentials, target <c>ScanlineStudio/&lt;key&gt;</c>, persisted
/// <c>CRED_PERSIST_LOCAL_MACHINE</c> (per user, this machine, not roamed), blob = UTF-16LE of the secret.
/// Windows never prompts for these, so <c>allowPrompt</c> is ignored. Calls run on the thread pool;
/// <c>CredRead</c> goes through LSA and must not run on the UI thread.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE.</summary>
    public const int MaxBlobBytes = 5 * 512;

    internal const string TargetPrefix = "ScanlineStudio/";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    private readonly ILogger _logger;

    public WindowsCredentialStore(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsSecure => true;

    public string BackendName => "Windows Credential Manager";

    /// <summary>False when the logon session has no credential store (ERROR_NO_SUCH_LOGON_SESSION, e.g. some
    /// service/network logons) or the API fails outright. Reads a target that normally does not exist.</summary>
    public static bool IsAvailable()
    {
        var error = TryRead(TargetPrefix + "__probe__", out _);
        return error is 0 or ErrorNotFound;
    }

    public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var error = TryRead(TargetPrefix + key, out var secret);
            return error switch
            {
                0 => CredentialRead.Found(secret!),
                ErrorNotFound => CredentialRead.Absent,
                ErrorNoSuchLogonSession => CredentialRead.Unavailable("no credential store for this logon session"),
                _ => Unavailable(error, "read"),
            };
        }, ct);

    public Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var blob = Encoding.Unicode.GetBytes(secret);
            try
            {
                if (blob.Length > MaxBlobBytes)
                {
                    return CredentialWrite.Failed($"secret is {blob.Length} bytes; the Windows Credential Manager limit is {MaxBlobBytes}");
                }

                var error = Write(TargetPrefix + key, blob);
                return error switch
                {
                    0 => CredentialWrite.Success,
                    ErrorNoSuchLogonSession => CredentialWrite.Unavailable("no credential store for this logon session"),
                    _ => Failed(error, "write"),
                };
            }
            finally
            {
                Array.Clear(blob);
            }
        }, ct);

    public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (NativeMethods.CredDelete(TargetPrefix + key, CredTypeGeneric, 0))
            {
                return CredentialWrite.Success;
            }

            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                ErrorNotFound => CredentialWrite.Success,
                ErrorNoSuchLogonSession => CredentialWrite.Unavailable("no credential store for this logon session"),
                _ => Failed(error, "delete"),
            };
        }, ct);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.Run(() => TryRead(TargetPrefix + key, out _) == 0, ct);

    private CredentialRead Unavailable(int error, string operation)
    {
        Log.OperationFailed(_logger, operation, error);
        return CredentialRead.Unavailable($"Windows error {error}");
    }

    private CredentialWrite Failed(int error, string operation)
    {
        Log.OperationFailed(_logger, operation, error);
        return CredentialWrite.Failed($"Windows error {error}");
    }

    /// <summary>0 on success (secret set), else the Win32 error.</summary>
    private static unsafe int TryRead(string target, out string? secret)
    {
        secret = null;
        if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out var credentialPtr))
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            var credential = (NativeMethods.CredentialW*)credentialPtr;
            secret = credential->CredentialBlobSize == 0
                ? string.Empty
                : Encoding.Unicode.GetString(new ReadOnlySpan<byte>((void*)credential->CredentialBlob, (int)credential->CredentialBlobSize));
            return 0;
        }
        finally
        {
            NativeMethods.CredFree(credentialPtr);
        }
    }

    private static unsafe int Write(string target, byte[] blob)
    {
        var targetPtr = Marshal.StringToHGlobalUni(target);
        try
        {
            fixed (byte* blobPtr = blob)
            {
                var credential = new NativeMethods.CredentialW
                {
                    Type = CredTypeGeneric,
                    TargetName = targetPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)blobPtr,
                    Persist = CredPersistLocalMachine,
                };
                return NativeMethods.CredWrite(&credential, 0) ? 0 : Marshal.GetLastPInvokeError();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
        }
    }

    private static partial class NativeMethods
    {
        /// <summary>CREDENTIALW, blittable: every string/pointer field as IntPtr, FILETIME as two DWORDs.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CredentialW
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public uint LastWrittenLow;
            public uint LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool CredWrite(CredentialW* credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial void CredFree(IntPtr buffer);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Windows Credential Manager {Operation} failed with Win32 error {Error}")]
        public static partial void OperationFailed(ILogger logger, string operation, int error);
    }
}
