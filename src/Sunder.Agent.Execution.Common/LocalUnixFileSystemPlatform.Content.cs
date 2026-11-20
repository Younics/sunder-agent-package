namespace Sunder.Agent.Execution.Common;

internal sealed partial class LocalUnixFileSystemPlatform
{
    public LocalSecureHandle CreateUserFile(LocalSecureHandle parent, string name)
    {
        ValidateName(name);
        RequireDirectory(parent);
        var path = Path.Combine(parent.FullPath, name);
        var descriptor = LocalUnixNative.OpenAt(parent.Handle.DangerousGetHandle().ToInt32(), name,
            LocalUnixNative.ReadWrite | LocalUnixNative.CreateFlag | LocalUnixNative.ExclusiveFlag
            | LocalUnixNative.NoFollowFlag | LocalUnixNative.CloseOnExecFlag | LocalUnixNative.NonBlockingFlag,
            0x1b6); // 0666, subject to umask and filesystem/default-ACL creation rules.
        if (descriptor < 0)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error == 17) throw new LocalSecureFileAlreadyExistsException(path);
            throw LocalUnixNative.Error("openat exclusive user file", error);
        }
        try { return CreateHandle(descriptor, path, requireDirectory: false); }
        catch (Exception exception)
        {
            // CreateHandle owns/closes descriptor on failure. An entry may exist;
            // it is not safe to infer its cleanup identity by its name.
            throw new LocalSecureIncompleteMutationException(path, exception);
        }
    }

    public LocalSecureHandle OpenFileForContentUpdate(
        LocalSecureHandle parent, string name, LocalSecureHandle expectedTarget)
    {
        if (expectedTarget.Kind != LocalSecureNodeKind.RegularFile)
            throw new LocalSecureApprovalChangedException(expectedTarget.FullPath);
        var target = OpenChild(parent, name, parent.Identity.Volume, requireDirectory: false, writable: true);
        if (target.Kind != LocalSecureNodeKind.RegularFile || target.Identity != expectedTarget.Identity)
        {
            target.Dispose();
            throw new LocalSecureApprovalChangedException(expectedTarget.FullPath);
        }
        return target;
    }

    public LocalSecureHandle RetainContentTarget(LocalSecureHandle target)
    {
        // dup preserves the opened object, not a name. Refresh its length/mode
        // rather than carrying the pre-write LocalSecureHandle snapshot forward.
        var retained = CreateHandle(LocalUnixNative.Duplicate(target.Handle.DangerousGetHandle().ToInt32()),
            target.FullPath, requireDirectory: false);
        if (retained.Kind != LocalSecureNodeKind.RegularFile || retained.Identity != target.Identity)
        {
            retained.Dispose();
            throw new LocalSecureApprovalChangedException(target.FullPath);
        }
        return retained;
    }
}
