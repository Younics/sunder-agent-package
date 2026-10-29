using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class SectionHelpButton : Button
{
    public static readonly StyledProperty<string?> HelpTextProperty =
        AvaloniaProperty.Register<SectionHelpButton, string?>(nameof(HelpText));

    public string? HelpText
    {
        get => GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public SectionHelpButton()
    {
        Classes.Add("agent-info");
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M7,0.75 A6.25,6.25 0 1 1 6.99,0.75 M7,6.25 L7,10 M7,3.75 L7,4"),
            StrokeThickness = 1.25, StrokeLineCap = PenLineCap.Round,
        };
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        Content = icon;
        AutomationProperties.SetName(this, "More information");
        var text = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(HelpText)) { Source = this });
        text.Bind(MaxWidthProperty, this.GetResourceObservable("Sunder.Agent.HelpWidth"));
        text.Bind(FontSizeProperty, this.GetResourceObservable("Sunder.FontSize.Body"));
        text.Bind(ForegroundProperty, this.GetResourceObservable("Sunder.Brush.Foreground.Primary"));
        Flyout = new Flyout { Content = text, FlyoutPresenterClasses = { "agent-help-popover" } };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HelpTextProperty)
        {
            SetCurrentValue(IsVisibleProperty, !string.IsNullOrWhiteSpace(HelpText));
            AutomationProperties.SetHelpText(this, HelpText);
        }
    }
}
