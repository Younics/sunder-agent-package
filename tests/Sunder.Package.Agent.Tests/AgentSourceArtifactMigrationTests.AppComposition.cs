using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static string SingleProjectRoleOutput(string root, string role)
        => Path.Combine(root, "bin/Debug/net10.0/sunder-roles", role, RuntimeInformation.RuntimeIdentifier);

    private static void AssertHeadlessRoleOutput(string root)
    {
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "obj/Debug/net10.0/sunder-analysis",
            RuntimeInformation.RuntimeIdentifier, "sunder-rpc/package-role-plan.v1.json")));
        Assert.False(plan.RootElement.GetProperty("hasApp").GetBoolean());
        Assert.Empty(plan.RootElement.GetProperty("appCompile").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(SingleProjectRoleOutput(root, "app"), "sunder-dev")));
        var output = SingleProjectRoleOutput(root, "worker");
        Assert.True(Directory.Exists(Path.Combine(output, "sunder-dev")));
        Assert.DoesNotContain(Directory.EnumerateFiles(output, "*.dll", SearchOption.AllDirectories), path =>
            Path.GetFileName(path) is "Sunder.Sdk.Avalonia.dll" or "CommunityToolkit.Mvvm.dll"
            || Path.GetFileName(path).StartsWith("Avalonia", StringComparison.Ordinal));
    }

    internal static ServiceProvider CreateGeneratedApp(Assembly app, IPackageContext context, ISunderRpcClient rpc,
        IBackgroundTaskQueue? background = null, IPackageWorkerExecutionObservations? executionObservations = null)
    {
        Assert.DoesNotContain(app.GetTypes(), type => type.Name == "AppPackageModule");
        var module = (ISunderAppPackageModule)Activator.CreateInstance(app.GetType("Sunder.Generated.Package.AppModule", true)!)!;
        var services = new ServiceCollection();
        module.ConfigureAppServices(services, context);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IPackageContext) || service.ServiceType == typeof(ISunderRpcClient)
            || service.ServiceType == typeof(IPackageNotificationService) || service.ServiceType == typeof(IBackgroundTaskQueue)
            || service.ServiceType == typeof(IPackageSettingsNavigationService) || service.ServiceType == typeof(IPackageShellViewService)
            || service.ServiceType == typeof(IPackageDevelopmentService) || service.ServiceType == typeof(IPackageLogNavigationService)
            || service.ServiceType == typeof(IPackageAppStateUpdates) || service.ServiceType == typeof(IPackageWorkerExecutionObservations));
        services.AddSingleton(context); services.AddSingleton(rpc);
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
        services.AddSingleton<IPackageShellViewService>(new CapturingHistoryShellViewService());
        services.AddSingleton<IPackageDevelopmentService>(new CompositionDevelopmentHost());
        services.AddSingleton<IPackageLogNavigationService>(new CompositionDevelopmentHost());
        services.AddSingleton<IPackageAppStateUpdates, BuilderUiStateUpdates>();
        if (executionObservations is not null) services.AddSingleton(executionObservations);
        if (background is not null) services.AddSingleton(background);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task VerifyGeneratedAppView(string root, string family)
    {
        var output = SingleProjectRoleOutput(root, "app");
        var name = family == "Agent" ? "Sunder.Package.Agent.App" : "Sunder.Package.Agent." + family + ".App";
        var probe = Directory.CreateDirectory(Path.Combine(root, "app-view-probe")).FullName;
        File.Copy(Path.Combine(BuilderSourceMigrationTests.FindRepository(), "tests/Sunder.Package.Agent.Tests/Fixtures/GeneratedAppViewProbe.cs.txt"), Path.Combine(probe, "Program.cs"));
        await File.WriteAllTextAsync(Path.Combine(probe, "Probe.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Avalonia.Headless" Version="12.0.3" />
                <PackageReference Include="Avalonia.Themes.Fluent" Version="12.0.3" />
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
                {{(family == "Mcp" ? "<PackageReference Include=\"Avalonia.AvaloniaEdit\" Version=\"12.0.0\" /><PackageReference Include=\"AvaloniaEdit.TextMate\" Version=\"12.0.0\" />" : "")}}
                {{(family is "Subagents" or "Agent" ? "<PackageReference Include=\"Avalonia.Controls.ItemsRepeater\" Version=\"12.0.0\" /><PackageReference Include=\"LiveMarkdown.Avalonia\" Version=\"2.2.0\" />" : "")}}
                {{string.Join("", new[] { name, "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Microsoft.Extensions.Logging.Abstractions" }
                    .Select(reference => $"<Reference Include=\"{reference}\" HintPath=\"{Escape(Path.Combine(output, reference + ".dll"))}\" />"))}}
              </ItemGroup>
            </Project>
            """);
        await Run(probe, "restore", "Probe.csproj", "--disable-parallel");
        await Run(probe, "run", "--project", "Probe.csproj", "--no-restore", "--no-launch-profile", "--", name,
            AgentPackageRepositoryInventory.GetRuntimePackageId(name[..^4]));
    }

    private sealed class AppCallbackProbe(string packageId, string handlerId, IReadOnlyDictionary<string, string>? expectedParameters = null) : IPackageCallbackClient
    {
        private readonly PackageCallbackSessionStatus _pending = new(packageId, handlerId,
            "generated-app-session", PackageCallbackSessionState.Pending, "Continue in browser.", new Uri("https://auth.example/authorize"), DateTimeOffset.UtcNow.AddMinutes(5));
        internal TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts, Opens, Cancellations;
        public bool IsAvailable => true;
        public ValueTask<PackageCallbackSessionStatus> StartAsync(string handler, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal(_pending.CallbackHandlerId, handler);
            if (expectedParameters is null) Assert.Null(parameters);
            else { Assert.NotNull(parameters); Assert.Equal(expectedParameters.OrderBy(pair => pair.Key), parameters.OrderBy(pair => pair.Key)); }
            Starts++; return ValueTask.FromResult(_pending);
        }
        public ValueTask<PackageCallbackSessionStatus> GetStatusAsync(string session, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Assert.Equal(_pending.CallbackSessionId, session); Polled.TrySetResult(); return ValueTask.FromResult(_pending); }
        public ValueTask<bool> CancelAsync(string session, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Assert.Equal(_pending.CallbackSessionId, session); Cancellations++; Cancelled.TrySetResult(); return ValueTask.FromResult(true); }
        public ValueTask OpenLaunchUriAsync(Uri uri, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Assert.Equal(_pending.LaunchUri, uri); Opens++; return ValueTask.CompletedTask; }
        public ValueTask<PackageCallbackSessionStatus> WaitForCompletionAsync(string session, TimeSpan timeout, CancellationToken token = default)
            => throw new InvalidOperationException("The bounded callback runner owns polling.");
    }
}
