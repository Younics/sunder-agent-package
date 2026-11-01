using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Tests;

internal class TestPackageRoleLocalWorkspace(string rootPath)
    : Sunder.Package.Agent.TestSupport.TestPackageRoleLocalWorkspace(rootPath);

internal class TestPackageFileStoreBase(string rootPath) : DirectoryPackageFileStore(rootPath);

internal class EmptyPackageSettings : NoOpPackageSettings;

internal class InMemoryPackageSecrets : Sunder.Package.Agent.TestSupport.InMemoryPackageSecrets;

internal static class TestPackageStorageGuards
{
    internal static void Key(string? key)
    {
        TestPackageStorageValidation.Key(key);
    }

    internal static void Prefix(string? prefix)
    {
        TestPackageStorageValidation.Prefix(prefix);
    }

    internal static void Value(string? value)
    {
        TestPackageStorageValidation.Value(value);
    }

    internal static void RelativePath(string? relativePath)
    {
        TestPackageStorageValidation.RelativePath(relativePath);
    }

    internal static void FileLength(long byteCount)
    {
        TestPackageStorageValidation.FileLength(byteCount);
    }
}
