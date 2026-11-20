using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.Shared.Importing;

internal static partial class BoundedImportIO
{
    private sealed class OpenedImportFile(FileStream stream, ImportNodeIdentity identity) : IAsyncDisposable, IDisposable
    {
        public FileStream Stream { get; } = stream;
        public ImportNodeIdentity Identity { get; } = identity;
        public void Dispose() => Stream.Dispose();
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }

    private sealed class OpenedImportNode(SafeFileHandle handle, ImportNodeIdentity identity) : IDisposable
    {
        private SafeFileHandle? _handle = handle;
        public SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(OpenedImportNode));
        public ImportNodeIdentity Identity { get; } = identity;

        public SafeFileHandle DetachHandle()
        {
            var handle = Handle;
            _handle = null;
            return handle;
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
}
