using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class SectionHeader : Grid
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SectionHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> HelpTextProperty = AvaloniaProperty.Register<SectionHeader, string?>(nameof(HelpText));
    public static readonly StyledProperty<bool> IsPageTitleProperty = AvaloniaProperty.Register<SectionHeader, bool>(nameof(IsPageTitle));
    private readonly TextBlock _title;
    private readonly SectionHelpButton _help;
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? HelpText { get => GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }
    public bool IsPageTitle { get => GetValue(IsPageTitleProperty); set => SetValue(IsPageTitleProperty, value); }

    public SectionHeader()
    {
        ColumnDefinitions = new("Auto,Auto");
        this.Bind(ColumnSpacingProperty, this.GetResourceObservable("Sunder.Spacing.XSmall"));
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _title = title;
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        title.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("Sunder.FontSize.SectionTitle"));
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Sunder.Brush.Foreground.Primary"));
        var help = new SectionHelpButton { IsVisible = false };
        _help = help;
        help.Bind(SectionHelpButton.HelpTextProperty, new Binding(nameof(HelpText)) { Source = this });
        SetColumn(help, 1);
        Children.Add(title);
        Children.Add(help);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var icon = _help.IsVisible && this.TryFindResource("Sunder.Agent.IconSize", out var resource) && resource is double size ? size : 0;
        _title.MaxWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width - icon - (icon > 0 ? ColumnSpacing : 0))
            : double.PositiveInfinity;
        return base.MeasureOverride(availableSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPageTitleProperty)
            _title.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable(IsPageTitle ? "Sunder.FontSize.PageTitle" : "Sunder.FontSize.SectionTitle"));
    }
}
