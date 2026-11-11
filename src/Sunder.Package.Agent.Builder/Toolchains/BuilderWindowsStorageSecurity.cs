using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
[SupportedOSPlatform("windows")]
internal static class BuilderWindowsStorageSecurity
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const AccessControlSections Sections = AccessControlSections.Access | AccessControlSections.Owner;
    private const int UnsafeAncestorRights = (int)(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes) | 0x10000000 | 0x40000000;

    internal static void CreatePrivateDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(User(identity), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // Set the protected DACL at creation, never after a public staging window.
        // Do not set owner/group: they remain the creating token's defaults.
        new DirectoryInfo(path).Create(security);
    }

    internal static FileStream OpenPrivateFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(User(identity), FileSystemRights.FullControl, AccessControlType.Allow));
        var rights = access switch
        {
            FileAccess.Read => FileSystemRights.Read,
            FileAccess.Write => FileSystemRights.Write,
            FileAccess.ReadWrite => FileSystemRights.Read | FileSystemRights.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };
        return new FileInfo(path).Create(mode, rights, share, 64 * 1024, FileOptions.None, security);
    }

    internal static void RequireSafeAncestor(string path)
        => RequireSecurity(new DirectoryInfo(path).GetAccessControl(Sections), path, privateContent: false);

    internal static void RequirePrivateDirectory(string path)
        => RequireSecurity(new DirectoryInfo(path).GetAccessControl(Sections), path, privateContent: true);

    internal static void RequirePrivateFile(string path)
        => RequireSecurity(new FileInfo(path).GetAccessControl(Sections), path, privateContent: true);

    private static void RequireSecurity(FileSystemSecurity security, string path, bool privateContent)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = User(identity);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !Trusted(descriptor.Owner, user))
            throw Unsafe(path, "has an untrusted owner");
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
            throw Unsafe(path, "has an unrestricted DACL");
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            // An inherit-only grant cannot change this ancestor, but on private
            // storage it can expose the SDK's next file or subdirectory.
            if (!privateContent && (ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not CommonAce common || common.IsCallback)
                throw Unsafe(path, "has an unsupported access rule");
            if (common.AceQualifier == AceQualifier.AccessDenied) continue;
            if (common.AceQualifier != AceQualifier.AccessAllowed)
                throw Unsafe(path, "has an unsupported access rule");
            if (!Trusted(common.SecurityIdentifier, user)
                && (privateContent ? common.AccessMask != 0 : (common.AccessMask & UnsafeAncestorRights) != 0))
                throw Unsafe(path, privateContent ? "is not private to its user" : "has an ancestor mutable by other users");
        }
    }

    private static SecurityIdentifier User(WindowsIdentity identity)
        => identity.User ?? throw new IOException("Builder cannot identify the Windows storage user.");

    private static bool Trusted(SecurityIdentifier principal, SecurityIdentifier user)
        => principal.Equals(user) || principal.IsWellKnown(WellKnownSidType.LocalSystemSid)
           || principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || principal.Value == TrustedInstallerSid;

    private static IOException Unsafe(string path, string reason)
        => new($"Builder storage '{path}' {reason}. Choose a private storage location; existing permissions were not changed.");
}
