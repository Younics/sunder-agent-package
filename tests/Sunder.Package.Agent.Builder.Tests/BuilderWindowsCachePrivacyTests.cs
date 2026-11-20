extern alias BuilderWorker;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class BuilderWindowsCachePrivacyTests
{
    [WindowsFact("Windows cache privacy requires real security descriptors.")]
    public async Task NewCacheAndStateFilesArePrivateWithoutOverridingTokenOwnerOrGroup()
    {
        using var fixture = new Fixture();
        var home = await fixture.HomeAsync();
        var reference = new DirectoryInfo(fixture.Root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
        foreach (var path in new[] { fixture.Options.DotnetTemplateCacheRootPath!, home })
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(reference.GetOwner(typeof(SecurityIdentifier)), security.GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal(reference.GetGroup(typeof(SecurityIdentifier)), security.GetGroup(typeof(SecurityIdentifier)));
            AssertPrivate(security);
        }
        foreach (var path in Directory.EnumerateFiles(home))
        {
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(reference.GetOwner(typeof(SecurityIdentifier)), security.GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal(reference.GetGroup(typeof(SecurityIdentifier)), security.GetGroup(typeof(SecurityIdentifier)));
            AssertPrivate(security);
        }
        Assert.Equal(home, await fixture.HomeAsync());
    }

    [WindowsTheory("Windows cache admission must inspect real DACLs without repairing existing state.")]
    [InlineData("ancestor")]
    [InlineData("base")]
    [InlineData("home")]
    [InlineData("owner")]
    [InlineData("ready")]
    [InlineData("lock")]
    [InlineData("inherit-only")]
    [InlineData("nested-directory")]
    [InlineData("nested-file")]
    public async Task ForeignPermissionsRejectAdmissionWithoutChangingTheDescriptor(string location)
    {
        using var fixture = new Fixture();
        var home = await fixture.HomeAsync();
        var nested = Path.Combine(home, ".templateengine");
        if (location is "nested-directory" or "nested-file") Directory.CreateDirectory(nested);
        if (location == "nested-file") await File.WriteAllTextAsync(Path.Combine(nested, "packages.json"), "untouched");
        var path = location switch
        {
            "ancestor" => fixture.Root,
            "base" => fixture.Options.DotnetTemplateCacheRootPath!,
            "home" => home,
            "owner" => Path.Combine(home, BuilderTemplateCache.OwnerName),
            "ready" => Path.Combine(home, BuilderTemplateCache.ReadyName),
            "lock" => Path.Combine(home, BuilderTemplateCache.LockName),
            "inherit-only" => home,
            "nested-directory" => nested,
            "nested-file" => Path.Combine(nested, "packages.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        var directory = Directory.Exists(path);
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var rights = location == "ancestor" ? FileSystemRights.DeleteSubdirectoriesAndFiles : FileSystemRights.Read;
        var inheritance = location == "inherit-only" ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var propagation = location == "inherit-only" ? PropagationFlags.InheritOnly : PropagationFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights,
            inheritance, propagation, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
        var before = SecurityBytes(path, directory);

        await Assert.ThrowsAsync<IOException>(() => fixture.HomeAsync());

        Assert.Equal(before, SecurityBytes(path, directory));
        Assert.True(Directory.Exists(home));
        Assert.True(File.Exists(Path.Combine(home, BuilderTemplateCache.OwnerName)));
        if (location == "nested-file") Assert.Equal("untouched", await File.ReadAllTextAsync(path));
    }

    [WindowsFact("An absent Windows DACL grants access; an empty rule enumeration is not proof of privacy.")]
    public async Task NullDaclCannotBeAdoptedAsPrivateCacheState()
    {
        using var fixture = new Fixture();
        var home = await fixture.HomeAsync();
        var directory = new DirectoryInfo(home);
        var security = directory.GetAccessControl();
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0) { DiscretionaryAcl = null };
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        directory.SetAccessControl(security);
        var before = SecurityBytes(home, directory: true);

        await Assert.ThrowsAsync<IOException>(() => fixture.HomeAsync());

        Assert.Equal(before, SecurityBytes(home, directory: true));
    }

    private static void AssertPrivate(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.Contains(rules, rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(identity.User));
        Assert.All(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow), rule => Assert.Equal(identity.User, rule.IdentityReference));
    }

    private static byte[] SecurityBytes(string path, bool directory)
        => (directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl())
            .GetSecurityDescriptorBinaryForm();

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = TemporaryDirectory.Create("builder-win-cache");
        internal string Root => _temporary.Path;
        internal BuilderProjectDriverOptions Options => new(Path.Combine(Root, "tools"))
        { DotnetTemplateVersion = "1.1.0", DotnetTemplateCacheRootPath = Path.Combine(Root, "templates") };

        internal async Task<string> HomeAsync()
        {
            using var cache = await BuilderTemplateCache.AcquireAsync(Options, "10.0.403", default);
            await cache.MarkReadyAsync(default);
            return cache.HomePath;
        }

        public void Dispose() { _temporary.Dispose(); _temporary.AssertCleaned(); }
    }
}
