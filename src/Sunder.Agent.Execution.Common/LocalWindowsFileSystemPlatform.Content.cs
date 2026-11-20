namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalWindowsFileSystemPlatform
{
    private const uint ContentReadAccess = LocalWindowsNative.ReadData
                                           | LocalWindowsNative.ReadAttributes
                                           | LocalWindowsNative.ReadControl;

    public LocalSecureHandle CreateUserFile(LocalSecureHandle parent, string name)
    {
        ValidateName(name);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, name);
        // No authored security descriptor: use the token and parent's normal
        // creation policy. Never use this API for private challenge material.
        var native = LocalWindowsNative.OpenRelative(parent.Handle, name,
            ContentReadAccess | LocalWindowsNative.WriteData,
            requireDirectory: false, create: true, parent.CaseSensitiveDirectory, path,
            LocalWindowsNative.ShareRead);
        try
        {
            var created = CreateHandle(native, path, requireDirectory: false);
            RequireVolume(parent, created);
            return created;
        }
        catch (Exception exception)
        {
            native.Dispose();
            // Creation already happened. Do not claim it was undone or delete a
            // pathname after losing the creation handle.
            throw new LocalSecureIncompleteMutationException(path, exception);
        }
    }

    public LocalSecureHandle OpenFileForContentUpdate(
        LocalSecureHandle parent, string name, LocalSecureHandle expectedTarget)
    {
        ValidateName(name);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, name);
        if (expectedTarget.Kind != LocalSecureNodeKind.RegularFile)
            throw new LocalSecureApprovalChangedException(path);
        // FILE_OPEN, never overwrite/supersede. The share reservation excludes
        // competing ordinary writers and name changes while this handle lives.
        var native = LocalWindowsNative.OpenRelative(parent.Handle, name,
            ContentReadAccess | LocalWindowsNative.WriteData,
            requireDirectory: false, create: false, parent.CaseSensitiveDirectory, path,
            LocalWindowsNative.ShareRead);
        try
        {
            var target = CreateHandle(native, path, requireDirectory: false);
            RequireVolume(parent, target);
            RequireIdentity(expectedTarget, LocalWindowsNative.ReadMetadata(native, path));
            return target;
        }
        catch { native.Dispose(); throw; }
    }

    public LocalSecureHandle RetainContentTarget(LocalSecureHandle target)
    {
        var native = LocalWindowsNative.Reopen(target.Handle, ContentReadAccess,
            LocalWindowsNative.ShareAll, target.FullPath);
        try
        {
            var retained = CreateHandle(native, target.FullPath, requireDirectory: false);
            RequireIdentity(target, LocalWindowsNative.ReadMetadata(native, target.FullPath));
            return retained;
        }
        catch { native.Dispose(); throw; }
    }
}
