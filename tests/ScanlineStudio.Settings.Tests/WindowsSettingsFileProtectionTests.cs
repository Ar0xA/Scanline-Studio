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
/// <para><b>Device cost: none.</b> No audio, no serial port, no network. These create a uniquely
/// named subdirectory under the CONFIGURED settings directory and delete it again. They never read,
/// modify or overwrite the real <c>settings.json</c>.</para>
///
/// <para><b>Why <c>AppConfigPaths.ConfigDirectory</c> and not <c>%APPDATA%</c> directly.</b>
/// Relocation is a shipped feature. An operator who moved settings to a second drive or a network
/// share is precisely the configuration where <c>BUILTIN\Users</c> normally DOES have read — the one
/// case this assumption depends on, and the one a hardcoded <c>%APPDATA%</c> probe would report green
/// for. A temp path would be worse still: <c>Path.GetTempPath()</c> on Windows sits inside the
/// profile and inherits comparable protection, so it would pass for the wrong reason always.</para>
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

            using var identity = WindowsIdentity.GetCurrent();

            foreach (FileSystemAccessRule rule in rules)
            {
                // Only Allow rules that can actually READ the file matter. An inherited
                // Traverse-or-ReadAttributes ACE for BUILTIN\Users is benign and some corporate
                // policies set exactly that on profile roots -- flagging it would be a false alarm
                // saying "your password is exposed".
                if (rule.AccessControlType != AccessControlType.Allow
                    || (rule.FileSystemRights & FileSystemRights.ReadData) == 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;

                // A whitelist, deliberately. An enumerated deny-list would miss INTERACTIVE, Guests,
                // domain groups such as Domain Users -- which are not well-known SIDs at all -- and
                // any individual account explicitly granted read. SYSTEM and Administrators are
                // expected and not a finding: an administrator can take ownership regardless.
                var permitted = sid == identity.User
                    || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                    || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid);

                Assert.True(
                    permitted,
                    $"a file created where settings.json lives grants read to {Describe(sid)}. "
                    + "JsonSettingsStore skips owner-only permissions on Windows on the grounds that "
                    + "the settings directory is already private. On this machine it is not, and "
                    + "settings.json can hold a QRZ.com password in plaintext.");
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

            // Restricted deliberately: identity.Groups contains Everyone and Authenticated Users, so
            // a membership test would accept a file owned by Everyone while claiming it was owned by
            // the current user.
            Assert.True(
                owner is SecurityIdentifier sid
                    && (sid == identity.User
                        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)),
                $"a file created where settings.json lives is owned by {owner}, not by the current "
                + "user, so the private-directory assumption does not hold for this location.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return $"{sid.Translate(typeof(NTAccount))} ({sid})";
        }
        catch (IdentityNotMappedException)
        {
            return sid.ToString();
        }
    }

    /// <summary>
    /// Creates a uniquely named subdirectory beside where settings live and writes one file into it,
    /// so the ACL under test is the one settings.json would actually inherit — without touching a
    /// real settings file.
    /// </summary>
    private static (string Directory, string FilePath) CreateProbeFile()
    {
        // AppConfigPaths, not %APPDATA% directly. Relocation is a shipped feature, and an operator
        // who moved settings to a second drive or a share is exactly the case where BUILTIN\Users
        // normally DOES have read -- the one configuration this assumption depends on and the one a
        // hardcoded %APPDATA% probe would never see.
        var root = AppConfigPaths.ConfigDirectory;

        Assert.False(
            string.IsNullOrEmpty(root),
            "the configured settings directory resolved to nothing, so its ACL could not be read.");

        var directory = Path.Combine(root, $"ScanlineStudio-acl-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, "settings.json");
        File.WriteAllText(filePath, "{}");

        return (directory, filePath);
    }
}
