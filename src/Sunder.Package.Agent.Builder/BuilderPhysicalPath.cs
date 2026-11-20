using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.Builder;

// Canonical source names for inspection and containment classification, not a
// retained mutation capability. Callers still validate their source/owner revision.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderPhysicalPath
{
    // Read-only classification before capture: preserve the spelling of the
    // nearest existing entry and append only the genuinely missing suffix.
    // Callers enforce their original-link policy separately; never normalize an
    // already-approved identity through this helper for positive authority.
    internal static string ReadLocation(string path)
    {
        var current = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) _ = new UTF8Encoding(false, true).GetByteCount(current);
        var missing = new Stack<string>();
        while (true)
        {
            try { _ = File.GetAttributes(current); break; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Push(Path.GetFileName(current));
                current = Path.GetDirectoryName(current) ?? throw new IOException("The Builder path has no existing filesystem root.", error);
            }
        }
        var physical = ReadExisting(current);
        return missing.Count == 0 ? physical : Path.Combine([physical, .. missing]);
    }

    internal static string ReadExisting(string path)
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(path);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Physical Builder paths require Windows, Linux or macOS.");

        // Never let a replacement encoding select a different native pathname.
        _ = new UTF8Encoding(false, true).GetByteCount(path);
        var resolved = RealPath(path, 0);
        if (resolved == 0) throw Error(path);
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The physical source path is unavailable."); }
        finally { Free(resolved); }
    }

    private static string ReadWindows(string path)
    {
        // Query only: no write/delete authority and no change to source sharing.
        // POSIX semantics respect the actual parent directory's case policy.
        using var handle = OpenFile(path, 0, 7, 0, 3, 0x02000000 | 0x01000000, 0);
        if (handle.IsInvalid) throw Error(path);
        var capacity = 512;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = FinalPath(handle, buffer, checked((uint)capacity), 0);
            if (length == 0) throw Error(path);
            if (length < capacity)
            {
                var result = buffer.ToString();
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
                if (result.Length >= 7 && result.StartsWith(@"\\?\", StringComparison.Ordinal)
                    && char.IsAsciiLetter(result[4]) && result[5] == ':' && result[6] == '\\') return result[4..];
                throw new IOException("The physical Builder source has no supported DOS or UNC path.");
            }
            if (length > 32768) throw new PathTooLongException("The physical Builder source path exceeds the Windows path budget.");
            capacity = checked((int)length + 1);
        }
        throw new IOException("The physical Builder source path changed while it was being resolved.");
    }

    private static IOException Error(string path)
        => new($"Cannot resolve the physical Builder path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFile(string path, uint access, uint share, nint security,
        uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
