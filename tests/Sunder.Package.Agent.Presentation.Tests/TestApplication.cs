using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(Sunder.Package.Agent.Presentation.Tests.TestApplicationBuilder))]

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class TestApplicationBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

internal sealed class TestApplication : Application;
