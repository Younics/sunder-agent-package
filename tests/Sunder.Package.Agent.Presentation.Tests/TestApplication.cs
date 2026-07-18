using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Sunder.Sdk.Avalonia.Theming;

[assembly: AvaloniaTestApplication(typeof(Sunder.Package.Agent.Presentation.Tests.TestApplicationBuilder))]

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class TestApplicationBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

internal sealed class TestApplication : Application
{
    public override void Initialize()
    {
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
