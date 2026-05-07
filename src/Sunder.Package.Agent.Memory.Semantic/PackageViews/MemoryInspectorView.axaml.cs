using Avalonia.Controls;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public partial class MemoryInspectorView : UserControl
{
    public MemoryInspectorView()
    {
        InitializeComponent();
    }

    public MemoryInspectorView(MemoryInspectorService memoryInspectorService)
        : this()
    {
        DataContext = new MemoryInspectorViewModel(memoryInspectorService);
    }
}
