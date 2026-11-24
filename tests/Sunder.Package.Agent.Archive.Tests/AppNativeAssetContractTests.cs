using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AppNativeAssetContractTests
{
    [Theory]
    [InlineData("lib/libexample.so")]
    [InlineData("lib/runtimes/linux-x64/native/libexample.so")]
    public void ResolvedAssetMayUseEitherSupportedLayout(string logical)
        => Verify("payload/app/linux-x64/" + logical);

    [Theory]
    [InlineData(null)]
    [InlineData("payload/app/shared/lib/libexample.so")]
    [InlineData("payload/app/linux-x64/lib/runtimes/linux-arm64/native/libexample.so")]
    [InlineData("payload/app/linux-x64/lib/libexample.so", "payload/app/linux-x64/lib/runtimes/linux-x64/native/libexample.so")]
    public void MissingWrongRidSharedOrAmbiguousAssetsFail(string? first, string? second = null)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Verify([.. new[] { first, second }.OfType<string>()]));
        Assert.Contains("expected one exact-RID native asset 'libexample.so'", exception.Message, StringComparison.Ordinal);
    }

    private static void Verify(params string[] entries)
    {
        using var deps = JsonDocument.Parse("""
            {
              "runtimeTarget":{"name":"framework/linux-x64"},
              "targets":{"framework/linux-x64":{
                "NativeVendor/3.4.5":{"native":{"runtimes/linux-x64/native/libexample.so":{}}}
              }}
            }
            """);
        AppNativeAssetContract.Verify(deps.RootElement, "lib/Package.App.dll", "linux-x64", "test.package", entries);
    }
}
