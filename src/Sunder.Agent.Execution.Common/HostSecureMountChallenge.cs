using System.Text;

namespace Sunder.Agent.Execution.Common;

internal sealed class HostSecureMountChallenge : IDisposable
{
    public const string HostChallengeFileName = "host-challenge";
    public const string ContainerDirectoryName = "container-nested";
    public const string ContainerResponseFileName = "container-response";

    private readonly LocalSecureRoot _root;
    private readonly LocalSecureMutationReservation _cleanupReservation;
    private LocalSecureHandle? _directory;
    private LocalSecureHandle? _hostChallenge;
    private LocalSecureHandle? _containerDirectory;
    private LocalSecureHandle? _containerResponse;
    private bool _removed;

    private HostSecureMountChallenge(
        LocalSecureRoot root,
        string directoryName,
        LocalSecureMutationReservation cleanupReservation,
        LocalSecureHandle directory,
        LocalSecureHandle hostChallenge)
    {
        _root = root;
        DirectoryName = directoryName;
        _cleanupReservation = cleanupReservation;
        _directory = directory;
        _hostChallenge = hostChallenge;
    }

    public string DirectoryName { get; }

    public static HostSecureMountChallenge Create(
        LocalSecureRoot root,
        string challenge,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Strict Docker mount identity challenges are unavailable on Windows.");
        }
        if (challenge.Length is < 32 or > 256 || !challenge.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A secure mount challenge must be bounded hexadecimal data.", nameof(challenge));
        }

        var reservation = root.Platform.ReserveMutationSlots(root.Handle, 2, cancellationToken);
        var directoryName = HostSecurePathEngine.CreateChallengeName();
        LocalSecureHandle? directory = null;
        LocalSecureHandle? challengeFile = null;
        try
        {
            root.Platform.CreateDirectory(root.Handle, directoryName);
            directory = root.Platform.OpenChild(
                root.Handle,
                directoryName,
                root.Volume,
                requireDirectory: true);
            challengeFile = root.Platform.CreateExclusiveFile(
                directory,
                HostChallengeFileName,
                Convert.ToUInt32("600", 8));
            WriteAll(challengeFile, challenge);
            root.Platform.Flush(challengeFile);
            root.Platform.Flush(directory);
            return new HostSecureMountChallenge(
                root,
                directoryName,
                reservation,
                directory,
                challengeFile);
        }
        catch
        {
            challengeFile?.Dispose();
            if (directory is not null)
            {
                TryHide(root, directoryName, directory, reservation);
                directory.Dispose();
            }
            reservation.Dispose();
            throw;
        }
    }

    public void VerifyContainerResponse(string expectedResponse)
    {
        var directory = _directory ?? throw new ObjectDisposedException(nameof(HostSecureMountChallenge));
        _containerDirectory = _root.Platform.OpenChild(
            directory,
            ContainerDirectoryName,
            _root.Volume,
            requireDirectory: true);
        _containerResponse = _root.Platform.OpenChild(
            _containerDirectory,
            ContainerResponseFileName,
            _root.Volume,
            requireDirectory: false,
            writable: true);
        var response = ReadBounded(_containerResponse, expectedResponse.Length + 1);
        if (!string.Equals(response, expectedResponse, StringComparison.Ordinal))
        {
            throw new LocalSecurePathException("The Docker mount identity challenge response did not match.");
        }
    }

    public void VerifyContainerCleanup()
    {
        var directory = _directory ?? throw new ObjectDisposedException(nameof(HostSecureMountChallenge));
        var hostChallenge = _hostChallenge ?? throw new ObjectDisposedException(nameof(HostSecureMountChallenge));
        var containerDirectory = _containerDirectory ?? throw new ObjectDisposedException(nameof(HostSecureMountChallenge));
        var containerResponse = _containerResponse ?? throw new ObjectDisposedException(nameof(HostSecureMountChallenge));
        var hostRemoved = _root.Platform.IsUnlinked(hostChallenge);
        var responseRemoved = _root.Platform.IsUnlinked(containerResponse);
        var challengePathRemoved = !_root.Platform.TryOpenChild(
            _root.Handle,
            DirectoryName,
            _root.Volume,
            requireDirectory: true,
            out var remainingDirectory);
        remainingDirectory?.Dispose();
        if (!hostRemoved || !responseRemoved || !challengePathRemoved)
        {
            throw new LocalSecurePathException(
                "The Docker daemon did not remove the exact mount identity challenge objects "
                + $"(host={hostRemoved}, response={responseRemoved}, path={challengePathRemoved}).");
        }
        _removed = true;
    }

    public void Dispose()
    {
        if (!_removed)
        {
            Zero(_containerResponse);
            Zero(_hostChallenge);
            if (_directory is not null && !_root.Platform.IsUnlinked(_directory))
            {
                TryHide(_root, DirectoryName, _directory, _cleanupReservation);
            }
        }
        _containerResponse?.Dispose();
        _containerResponse = null;
        _containerDirectory?.Dispose();
        _containerDirectory = null;
        _hostChallenge?.Dispose();
        _hostChallenge = null;
        _directory?.Dispose();
        _directory = null;
        _cleanupReservation.Dispose();
    }

    private static void TryHide(
        LocalSecureRoot root,
        string name,
        LocalSecureHandle directory,
        LocalSecureMutationReservation reservation)
    {
        try
        {
            root.Platform.DeleteEntry(
                root.Handle,
                name,
                directory: true,
                directory,
                finalTargetValidator: null,
                reservation,
                beforeMutationSyscall: static () => { },
                afterQuarantine: static () => { },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    private void Zero(LocalSecureHandle? handle)
    {
        if (handle is null || _root.Platform.IsUnlinked(handle))
        {
            return;
        }
        try
        {
            using var stream = new FileStream(handle.DuplicateHandle(), FileAccess.Write);
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
            _root.Platform.Flush(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteAll(LocalSecureHandle handle, string value)
    {
        using var stream = new FileStream(handle.DuplicateHandle(), FileAccess.Write);
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string ReadBounded(LocalSecureHandle handle, int maximumBytes)
    {
        using var stream = new FileStream(handle.DuplicateHandle(), FileAccess.Read);
        if (stream.Length < 0 || stream.Length > maximumBytes)
        {
            throw new LocalSecurePathException("The Docker mount identity response exceeded its bound.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Encoding.ASCII.GetString(bytes);
    }
}
