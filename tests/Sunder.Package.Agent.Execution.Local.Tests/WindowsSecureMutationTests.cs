using System.Runtime.Versioning;
using System.Text;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

// Direct native-backend qualification deliberately does not bypass or change the
// executor's production availability guard. These same methods are its backend.
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecureMutationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sunder-windows-mutations-").FullName;

    [PlatformFact(TestPlatform.Windows, "Native Windows access, directory flush and exact-handle rename qualification.")]
    public void NativeHandles_CreateFlushRenameAndDeleteWithoutPathFallbacks()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var platform = Assert.IsType<LocalWindowsFileSystemPlatform>(root.Platform);
        var parked = _root + "-parked";
        var parentMoved = false;
        try { Directory.Move(_root, parked); parentMoved = true; }
        catch (IOException) { }
        finally { if (parentMoved) Directory.Move(parked, _root); }
        Assert.False(parentMoved, "A retained Windows ancestor must deny delete sharing and prevent reparenting.");
        using var directory = platform.CreateDirectory(root.Handle, "created");
        using var created = platform.CreateExclusiveFile(directory, "private.txt", 0x180);
        RandomAccess.Write(created.Handle, "secret"u8, 0);
        platform.Flush(created);
        platform.Flush(directory);
        using var security = LocalWindowsNative.CaptureSecurity(created.Handle, created.FullPath);
        Assert.True(security.ProtectedDacl);
        Assert.NotEmpty(security.DaclBytes);
        Assert.Equal(1, BitConverter.ToUInt16(security.DaclBytes, 4)); // One explicit current-user ACE, no inherited grants.
        Assert.Throws<LocalSecureFileAlreadyExistsException>(() => platform.CreateExclusiveFile(directory, "private.txt", 0x180));
        Assert.Throws<IOException>(() => File.Move(created.FullPath, Path.Combine(directory.FullPath, "stolen.txt")));
        LocalWindowsNative.RenameNoReplace(created.Handle, directory.Handle, "published.txt", created.FullPath);
        platform.Flush(directory);
        using var read = platform.OpenChild(directory, "published.txt", root.Volume, requireDirectory: false);
        Assert.Equal(created.Identity, read.Identity);
        Assert.Equal("secret", Read(read));
        Assert.False(platform.IsUnlinked(created));
        LocalWindowsNative.Delete(created.Handle, created.FullPath);
        created.Dispose(); // POSIX name retirement occurs at this file object's close.
        platform.Flush(directory);
        Assert.True(platform.IsUnlinked(read));
        Assert.False(File.Exists(Path.Combine(directory.FullPath, "published.txt")));
    }

    [PlatformTheory(TestPlatform.Windows, "Windows publication and metadata preservation require native handles.")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Publish_RetainsExactTargetAndPreservesOriginalSecurity(bool overwrite, bool protectedOriginal)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var platform = root.Platform;
        var path = Path.Combine(_root, "target.txt");
        if (overwrite)
        {
            if (protectedOriginal)
            {
                using var original = platform.CreateExclusiveFile(root.Handle, "target.txt", 0x180);
                RandomAccess.Write(original.Handle, "original"u8, 0);
                platform.Flush(original);
            }
            else File.WriteAllText(path, "original");
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
        }
        using var expected = overwrite ? platform.OpenChild(root.Handle, "target.txt", root.Volume, false) : null;
        using var originalSecurity = expected is null ? null : LocalWindowsNative.CaptureSecurity(expected.Handle, path);
        using var reservation = platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(platform, root.Handle, reservation, "new-secret");
        using var stagedSecurity = LocalWindowsNative.CaptureSecurity(temporary.Handle, temporary.FullPath);
        var checkedPrivateStage = false;
        Publish(platform, root.Handle, temporary, "target.txt", expected, reservation, checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish) return;
            using var stillPrivate = LocalWindowsNative.CaptureSecurity(temporary.Handle, temporary.FullPath);
            Assert.True(stillPrivate.ProtectedDacl);
            Assert.Equal(stagedSecurity.DaclBytes, stillPrivate.DaclBytes);
            checkedPrivateStage = true;
        });
        using var retained = temporary.TakePublishedTarget();
        Assert.True(checkedPrivateStage);
        Assert.True(temporary.WasPublished);
        Assert.Equal(temporary.Identity, retained.Identity);
        Assert.Equal("new-secret", Read(retained));
        if (originalSecurity is not null)
        {
            using var publishedSecurity = LocalWindowsNative.CaptureSecurity(retained.Handle, path);
            Assert.Equal(originalSecurity.OwnerBytes, publishedSecurity.OwnerBytes);
            Assert.Equal(originalSecurity.GroupBytes, publishedSecurity.GroupBytes);
            Assert.Equal(originalSecurity.ProtectedDacl, publishedSecurity.ProtectedDacl);
            Assert.Equal(originalSecurity.DaclBytes, publishedSecurity.DaclBytes);
            Assert.Equal(originalSecurity.AccessPolicyBytes, publishedSecurity.AccessPolicyBytes);
            Assert.Equal(originalSecurity.AccessPolicyControl, publishedSecurity.AccessPolicyControl);
            Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive, File.GetAttributes(path));
        }
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), file => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(file)));
    }

    [PlatformTheory(TestPlatform.Windows, "Windows rollback must preserve old bytes and sanitize staged/new bytes.")]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterQuarantineBeforeMetadata)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforePostQuarantineSync)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterPublishedParentSync)]
    public void PublicationFailure_RestoresOriginalAndPermitsRetry(int checkpointValue)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using (var reservation = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None))
        using (var temporary = Stage(root.Platform, root.Handle, reservation, "SECRET"))
        {
            var invoked = false;
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected,
                reservation, checkpoint =>
                {
                    if (checkpoint != (LocalSecureFileSystemCheckpoint)checkpointValue) return;
                    invoked = true;
                    throw new LocalSecurePathException("Injected native mutation checkpoint failure.");
                }));
            Assert.True(invoked);
            root.Platform.DeleteTemporaryFile(root.Handle, temporary, reservation);
        }
        Assert.Equal("original", File.ReadAllText(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
        using var retrySlots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var retry = Stage(root.Platform, root.Handle, retrySlots, "retry");
        Publish(root.Platform, root.Handle, retry, "target.txt", expected, retrySlots);
        using var retained = retry.TakePublishedTarget();
        Assert.Equal("retry", Read(retained));
    }

    [PlatformTheory(TestPlatform.Windows, "No-replace publication must preserve a raced destination and original recovery bytes.")]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationRace_NeverOverwritesUnapprovedBytes(bool overwrite)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        if (overwrite) File.WriteAllText(path, "original");
        using var expected = overwrite ? root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false) : null;
        using var reservation = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, reservation, "SECRET");
        Assert.Throws<LocalSecureFileAlreadyExistsException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected,
            reservation, checkpoint =>
            {
                if (checkpoint == LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish) File.WriteAllText(path, "ATTACKER");
            }));
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, reservation);
        Assert.Equal(overwrite ? "original" : "ATTACKER", File.ReadAllText(path));
        if (overwrite)
            Assert.Contains(Directory.EnumerateFiles(_root).Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
                .Select(ReadPath), text => text == "ATTACKER");
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformTheory(TestPlatform.Windows, "The native source mutation must reject file, directory and hard-link substitutions without touching either identity.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SourceRace_RejectsReplacementWithoutMutatingEitherFile(int replacementKind)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        var path = Path.Combine(workspace, "target.txt");
        var moved = Path.Combine(workspace, "original.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "OUTSIDE");
        File.WriteAllText(path, "original");
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var reservation = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, reservation, "SECRET");
        var replaced = false;
        void Attempt() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected,
            reservation, checkpoint =>
            {
                if (checkpoint != LocalSecureFileSystemCheckpoint.BeforePublishSyscall) return;
                File.Move(path, moved);
                if (replacementKind == 1)
                {
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "sentinel.txt"), "ATTACKER");
                }
                else if (replacementKind == 2) TestFileSystemCapabilities.CreateHardLink(path, outside);
                else File.WriteAllText(path, "ATTACKER");
                replaced = true;
            });
        if (replacementKind == 0) Assert.Throws<LocalSecureApprovalChangedException>(Attempt);
        else Assert.ThrowsAny<LocalSecurePathException>(Attempt);
        Assert.True(replaced);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, reservation);
        Assert.Equal("original", File.ReadAllText(moved));
        Assert.Equal(replacementKind == 2 ? "OUTSIDE" : "ATTACKER",
            File.ReadAllText(replacementKind == 1 ? Path.Combine(path, "sentinel.txt") : path));
        Assert.Equal("OUTSIDE", File.ReadAllText(outside));
    }

    [PlatformTheory(TestPlatform.Windows, "Windows cancellation must not interrupt a reversible critical mutation.")]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterQuarantine_CompletesExactMutation(bool delete)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var cancellation = new CancellationTokenSource();
        using var reservation = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        if (delete)
            root.Platform.DeleteEntry(root.Handle, "target.txt", false, expected, candidate => Read(candidate) == "original", reservation,
                static () => { }, cancellation.Cancel, cancellation.Token);
        else
        {
            using var temporary = Stage(root.Platform, root.Handle, reservation, "new");
            Publish(root.Platform, root.Handle, temporary, "target.txt", expected, reservation, checkpoint =>
            {
                if (checkpoint == LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish) cancellation.Cancel();
            }, cancellation.Token);
            using var retained = temporary.TakePublishedTarget();
            Assert.Equal("new", Read(retained));
        }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(!delete, File.Exists(path));
        Assert.Equal("original", Read(expected));
    }

    [PlatformFact(TestPlatform.Windows, "Retained publication authority must permit exact-target patch compensation.")]
    public void PublishedHandle_RemainsValidAuthorityForASecondMutation()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        LocalSecureHandle retained;
        using (var slots = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None))
        using (var temporary = Stage(root.Platform, root.Handle, slots, "first"))
        {
            Publish(root.Platform, root.Handle, temporary, "target.txt", null, slots);
            retained = temporary.TakePublishedTarget();
        }
        using (retained)
        using (var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None))
        using (var temporary = Stage(root.Platform, root.Handle, slots, "second"))
        {
            Assert.Equal("first", File.ReadAllText(Path.Combine(_root, "target.txt")));
            Publish(root.Platform, root.Handle, temporary, "target.txt", retained, slots);
            using var final = temporary.TakePublishedTarget();
            Assert.Equal("second", Read(final));
            Assert.Equal("first", Read(retained));
        }
    }

    [PlatformTheory(TestPlatform.Windows, "Windows overwrites must preserve all data streams and sanitize copied streams on rollback.")]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternateStreams_PreserveContentAndSanitizeFailedPublication(bool failAfterPublication)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "retain-stream");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var chunkedBytes = Enumerable.Range(0, 131_077).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(path + ":文-😀", chunkedBytes);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var checkedLocks = false;
        var failureInjected = false;
        void Checkpoint(LocalSecureFileSystemCheckpoint checkpoint)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish)
            {
                // ADS sharing is separate from the default stream. The actual
                // copied stream must be locked too, not merely the main file.
                Assert.Throws<LocalSecurePathException>(() =>
                {
                    using var attempted = LocalWindowsNative.OpenRelative(temporary.Handle, ":metadata:$DATA",
                        LocalWindowsNative.GenericWrite, false, false, true, path, LocalWindowsNative.ShareAll);
                });
                checkedLocks = true;
            }
            if (failAfterPublication && checkpoint == LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)
            {
                failureInjected = true;
                throw new LocalSecurePathException("Injected failure after stream transfer.");
            }
        }
        if (failAfterPublication)
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint));
        else
            Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint);
        Assert.True(checkedLocks);
        Assert.Equal(failAfterPublication, failureInjected);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(failAfterPublication ? expected.Identity : temporary.Identity, final.Identity);
        Assert.Equal(failAfterPublication ? "original" : "SECRET", Read(final));
        Assert.Equal("retain-stream", ReadPath(path + ":metadata"));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", ReadPath(path + ":Zone.Identifier"));
        using (var data = File.OpenHandle(path + ":文-😀", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(chunkedBytes.LongLength, RandomAccess.GetLength(data));
            using var stream = new FileStream(data, FileAccess.Read);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(chunkedBytes, bytes.ToArray());
        }
        if (failAfterPublication)
        {
            Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
            var sanitized = Assert.Single(Directory.EnumerateFiles(_root), file => file != path);
            foreach (var suffix in new[] { ":metadata", ":Zone.Identifier", ":文-😀" })
            {
                using var data = File.OpenHandle(sanitized + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Assert.Equal(0, RandomAccess.GetLength(data));
            }
        }
    }

    [PlatformFact(TestPlatform.Windows, "A failed restore must report recovery-required without stranding new secret bytes.")]
    public void RestoreFailure_RetainsOriginalHiddenAndSanitizesNewPublication()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var restored = false;
        Assert.Throws<LocalSecureMutationRecoveryException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected,
            slots, checkpoint =>
            {
                if (checkpoint == LocalSecureFileSystemCheckpoint.BeforePostPublicationRestore) restored = true;
                if (checkpoint is LocalSecureFileSystemCheckpoint.AfterPublishedFileSync or LocalSecureFileSystemCheckpoint.BeforePostPublicationRestore)
                    throw new LocalSecurePathException("Injected publication/restore failure.");
            }));
        Assert.True(restored);
        Assert.False(File.Exists(path));
        Assert.Contains(Directory.EnumerateFiles(_root).Where(file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)))
            .Select(ReadPath), text => text == "original");
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformFact(TestPlatform.Windows, "Created directory cleanup must use its creation identity without sharing conflicts.")]
    public void CreatedDirectory_RetiresOnlyItsExactIdentity()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var ownership = new LocalCreatedDirectoryAuthority();
        using var child = ownership.CreateChild(root.Platform, root.Handle, "created", root.Volume);
        Assert.True(ownership.OwnsChild(root.Handle, child));
        ownership.Dispose();
        Assert.Empty(ownership.CleanupFailures);
        Assert.False(Directory.Exists(Path.Combine(_root, "created")));
        var retired = Assert.Single(Directory.EnumerateDirectories(_root));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retired)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(retired));
    }

    [PlatformFact(TestPlatform.Windows, "Windows reservations must retain the existing cancellation and capacity bounds.")]
    public void Reservation_CancelsAndRejectsCapacityBeforeCreatingTemporaryBytes()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => root.Platform.ReserveMutationSlots(root.Handle, 1, canceled.Token));
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
        for (var index = 0; index < HostSecurePathEngine.MaxQuarantineEntriesPerDirectory; index++)
            File.WriteAllText(Path.Combine(_root, $".sunder-secure-quarantine-{index:x8}"), string.Empty);
        Assert.Throws<LocalSecurePathException>(() => root.Platform.ReserveMutationSlots(root.Handle, 1, CancellationToken.None));
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), file => HostSecurePathEngine.IsTemporaryName(Path.GetFileName(file)));
    }

    [PlatformFact(TestPlatform.Windows, "Windows native rename must preserve exact UTF-16 names without encoding fallback.")]
    public void Publication_UsesExactUtf16ChildNames()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        foreach (var name in new[] { "文-😀-e\u0301.txt", "lone-" + new string('\ud800', 1) + ".txt" })
        {
            using var slots = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None);
            using var temporary = Stage(root.Platform, root.Handle, slots, "name-content");
            Publish(root.Platform, root.Handle, temporary, name, null, slots);
            using var retained = temporary.TakePublishedTarget();
            using var named = root.Platform.OpenChild(root.Handle, name, root.Volume, false);
            Assert.Equal(retained.Identity, named.Identity);
            Assert.Contains(root.Platform.EnumerateNames(root.Handle), actual => string.Equals(actual, name, StringComparison.Ordinal));
            Assert.Equal("name-content", Read(named));
        }
    }

    [PlatformFact(TestPlatform.Windows, "Cleanup authority is the retained creation handle, never a raced staging/alias name.")]
    public void LinkedStagingFile_IsRejectedAndOwnedBytesAreSanitizedWithoutDeletingTheAlias()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var alias = Path.Combine(_root, "outside-alias.txt");
        var sentinel = Path.Combine(_root, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "OUTSIDE");
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var linked = false;
        try { TestFileSystemCapabilities.CreateHardLink(alias, temporary.FullPath); linked = true; }
        catch (IOException)
        {
            // A filesystem may reject the link while our creation handle is open.
            Assert.False(File.Exists(alias));
        }
        if (linked)
            Assert.Throws<LocalSecureApprovalChangedException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", null, slots));
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        Assert.False(File.Exists(Path.Combine(workspace, "target.txt")));
        Assert.False(File.Exists(temporary.FullPath));
        if (linked) Assert.Equal(0, new FileInfo(alias).Length);
        Assert.Equal("OUTSIDE", File.ReadAllText(sentinel));
    }

    private static LocalSecureTemporaryFile Stage(ILocalSecureFileSystemPlatform platform, LocalSecureHandle parent,
        LocalSecureMutationReservation reservation, string text)
    {
        var temporary = platform.CreateTemporaryFile(parent, HostSecurePathEngine.CreateTemporaryName(), reservation);
        RandomAccess.Write(temporary.Handle, Encoding.UTF8.GetBytes(text), 0);
        LocalWindowsNative.Flush(temporary.Handle, temporary.FullPath);
        return temporary;
    }

    private static string Read(LocalSecureHandle handle)
    {
        using var stream = new FileStream(handle.DuplicateHandle(), FileAccess.Read);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadPath(string path)
    {
        // Test inspection must share the backend's still-retained write/delete
        // authority; ordinary File.ReadAllText would conflict with that handle.
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = new FileStream(file, FileAccess.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Publish(ILocalSecureFileSystemPlatform platform, LocalSecureHandle parent,
        LocalSecureTemporaryFile temporary, string targetName, LocalSecureHandle? expected,
        LocalSecureMutationReservation reservation, Action<LocalSecureFileSystemCheckpoint>? checkpoint = null,
        CancellationToken cancellationToken = default)
        => platform.PublishTemporaryFile(parent, temporary, targetName, overwrite: expected is not null,
            expected is null ? LocalSecureTargetExpectation.Missing(false) : LocalSecureTargetExpectation.Existing(expected.Identity, false),
            expected, expected is null ? null : candidate => Read(candidate) == "original" || Read(candidate) == "first",
            reservation,
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.BeforePublishSyscall),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.AfterQuarantineBeforeMetadata),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.BeforePostQuarantineSync),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.AfterNamedTemporaryCopyChunk),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.BeforeNamedTemporaryPublish),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.AfterPublishedFileSync),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.AfterPublishedParentSync),
            () => checkpoint?.Invoke(LocalSecureFileSystemCheckpoint.BeforePostPublicationRestore),
            cancellationToken);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
