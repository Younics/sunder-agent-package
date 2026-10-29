using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Reflection;
using Sunder.Sdk.Avalonia.Theming;

[assembly: AvaloniaTestApplication(typeof(Sunder.Package.Agent.Presentation.Tests.TestApplicationBuilder))]

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class TestApplicationBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var capture = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUNDER_UI_CAPTURE_ROOT"));
        var builder = AppBuilder.Configure<TestApplication>();
        if (capture)
        {
            // Skia comes from the source-App test closure, not the public SDK.
            // Keep ordinary NuGet-mode/headless tests free of that optional dependency.
            var extensions = Type.GetType("Avalonia.SkiaApplicationExtensions, Avalonia.Skia")
                ?? throw new InvalidOperationException("PNG capture requires the source-App test closure with Avalonia.Skia.");
            var useSkia = extensions.GetMethod("UseSkia", [typeof(AppBuilder)])
                ?? throw new InvalidOperationException("The selected Avalonia.Skia has no compatible renderer setup method.");
            useSkia.Invoke(null, [builder]);
        }
        return builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !capture });
    }
}

internal sealed class TestApplication : Application
{
    public override void Initialize()
    {
        // Optional scratch-run assertion for checking the production framework
        // bytes without changing dependency pins or the xUnit adapter version.
        if (Environment.GetEnvironmentVariable("SUNDER_TEST_AVALONIA_VERSION") is { Length: > 0 } expected
            && typeof(Avalonia.Controls.Control).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                .StartsWith(expected, StringComparison.Ordinal) != true)
            throw new InvalidOperationException($"The presentation test run did not load Avalonia {expected}.");
        RequestedThemeVariant = ThemeVariant.Dark;
        var baseUri = new Uri("avares://Sunder.Package.Agent.Presentation.Tests/");
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
        {
            Source = new Uri("avares://Sunder.Sdk.Avalonia/Themes/SunderThemeResources.axaml"),
        });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(baseUri)
        {
            Source = new Uri("avares://Sunder.Sdk.Avalonia/Themes/SunderPackageStyles.axaml"),
        });

        // Source-hosted captures use the real graphite palette, Fluent accent
        // aliases and shadows. Keep the portable NuGet-only fixture independent
        // of an optional App reference, just as with the optional Skia renderer.
        if (Type.GetType("Sunder.App.Services.ThemeManager, Sunder.App") is { } managerType)
        {
            var manager = managerType.GetConstructor([typeof(Application)])!.Invoke([this]);
            managerType.GetMethod("Initialize")!.Invoke(manager, null);
            return;
        }

        foreach (var field in typeof(SunderThemeKeys).GetFields()
                     .Where(field => field.IsLiteral
                                     && field.FieldType == typeof(string)
                                     && field.GetRawConstantValue() is string key
                                     && key.StartsWith("Sunder.Brush.", StringComparison.Ordinal)))
        {
            var key = (string)field.GetRawConstantValue()!;
            Resources[key] = new SolidColorBrush(ResolveBrushColor(key));
        }
    }

    private static Color ResolveBrushColor(string key)
        => key switch
        {
            SunderThemeKeys.BackgroundAppBrush => Color.Parse("#111214"),
            SunderThemeKeys.SurfaceBaseBrush => Color.Parse("#17191C"),
            SunderThemeKeys.SurfaceRaisedBrush => Color.Parse("#1D2024"),
            SunderThemeKeys.SurfacePopoverBrush => Color.Parse("#24282D"),
            SunderThemeKeys.SurfaceWorkspaceBrush => Color.Parse("#15171A"),
            SunderThemeKeys.SurfaceCodeBrush => Color.Parse("#101215"),
            SunderThemeKeys.ForegroundPrimaryBrush => Color.Parse("#F2F3F4"),
            SunderThemeKeys.ForegroundSecondaryBrush => Color.Parse("#C5C9CE"),
            SunderThemeKeys.ForegroundMutedBrush => Color.Parse("#8D949C"),
            SunderThemeKeys.ForegroundOnAccentBrush => Color.Parse("#17120A"),
            SunderThemeKeys.AccentBrush => Color.Parse("#D6A247"),
            SunderThemeKeys.SelectionBrush => Color.Parse("#66502F"),
            SunderThemeKeys.WarningBrush => Color.Parse("#E4B55B"),
            SunderThemeKeys.DangerBrush => Color.Parse("#E06C75"),
            SunderThemeKeys.SuccessBrush => Color.Parse("#7FBF8E"),
            SunderThemeKeys.TransparentBrush => Colors.Transparent,
            _ when key.Contains("Border", StringComparison.Ordinal) => Color.Parse("#3A3E44"),
            _ when key.Contains("Soft", StringComparison.Ordinal) => Color.Parse("#332C22"),
            _ when key.Contains("Overlay", StringComparison.Ordinal) => Color.Parse("#CC111214"),
            _ => Color.Parse("#2A2E33"),
        };
}
