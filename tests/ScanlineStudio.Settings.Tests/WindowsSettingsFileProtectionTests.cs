using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScanlineStudio.Settings.Tests;

/// <summary>
/// `BACKLOG.md` W6. On Unix, <c>JsonSettingsStore</c> creates <c>settings.json</c> with owner-only
/// permissions from the first byte, because it can carry a real QRZ.com password in plaintext. On
/// Windows it takes a plain <c>File.Create</c> and <c>TrySetOwnerOnlyPermissions</c> returns
/// immediately — deliberate, and documented as "the per-user profile ACL is already private".
///
/// <para><b>Nobody had verified that.</b> It is a security assumption resting on a comment. These
/// tests read the real ACL and check it, which is the difference between an assumption and a fact.</para>
///
/// <para><b>Device cost: none.</b> No audio, no serial port, no network. These create files under the
/// per-user application data directory and delete them again. They do not read, modify or overwrite
/// the real <c>settings.json</c> — every test uses its own uniquely named subdirectory, so a run
/// cannot disturb an installed configuration.</para>
///
/// <para><b>Why the profile directory and not a temp path.</b> The claim under test is specifically
/// about where settings actually live. <c>Path.GetTempPath()</c> on Windows is itself inside the user
/// profile and inherits comparable protection, so testing there would pass for the wrong reason and
/// would not catch a future move to a shared location.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSettingsFileProtectionTests
{
    [WindowsFact]
    public void AFileCreatedWhereSettingsLive_GrantsNoAccessToEveryoneOrAuthenticatedUsers()
    {
        var (directory, filePath) = CreateProbeFile();
        try
        {
            var rules = new FileInfo(filePath)
                .GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;

                // These three are the ones that would make a plaintext password readable by another
                // account on the same machine. SYSTEM and Administrators are expected and not a
                // finding -- an administrator can read any file regardless of this ACL.
                foreach (var wellKnown in new[]
                {
                    WellKnownSidType.WorldSid,
                    WellKnownSidType.AuthenticatedUserSid,
                    WellKnownSidType.BuiltinUsersSid,
                })
                {
                    Assert.False(
                        sid.IsWellKnown(wellKnown),
                        $"a file created where settings.json lives grants {rule.FileSystemRights} to "
                        + $"{wellKnown}. JsonSettingsStore skips owner-only permissions on Windows on "
                        + "the grounds that the profile ACL is already private. On this machine it is "
                        + "not, and settings.json can hold a QRZ.com password in plaintext.");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsFact]
    public void AFileCreatedWhereSettingsLive_IsOwnedByTheCurrentUser()
    {
        // A file the current user does not own is a different failure from a permissive ACL, and it
        // would mean the directory was created by something else -- worth separating so the message
        // says which of the two happened.
        var (directory, filePath) = CreateProbeFile();
        try
        {
            var owner = new FileInfo(filePath)
                .GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier));

            using var identity = WindowsIdentity.GetCurrent();

            Assert.True(
                owner is SecurityIdentifier sid
                    && (sid == identity.User || identity.Groups?.Contains(sid) == true),
                $"a file created where settings.json lives is owned by {owner}, not by the current "
                + "user, so the per-user profile assumption does not hold for this location.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Creates a uniquely named subdirectory beside where settings live and writes one file into it,
    /// so the ACL under test is the one settings.json would actually inherit — without touching a
    /// real settings file.
    /// </summary>
    private static (string Directory, string FilePath) CreateProbeFile()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.False(
            string.IsNullOrEmpty(root),
            "ApplicationData resolved to nothing, so the location settings live in could not be found.");

        var directory = Path.Combine(root, $"ScanlineStudio-acl-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, "settings.json");
        File.WriteAllText(filePath, "{}");

        return (directory, filePath);
    }
}
