using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class EditorSharedStateTests
{
    [Fact]
    public async Task ModelPicker_UsesPresentationRowsAndPreservesEffortAcrossSpeedChanges()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        using var state = new ModelBindingEditorState(new ProviderModelLoader(SelectionCatalog()), Options(), dispatcher);
        await state.RefreshAsync(new("provider", "model", "{\"reasoningVariantId\":\"max\"}"), TestContext.Current.CancellationToken);

        Assert.Equal(["Model", "Model · Fast"], state.Models.Select(model => model.Label));
        Assert.Equal(["Default", "Low", "Medium", "High", "Extra high", "Max"], state.ReasoningOptions.Select(option => option.Label));
        Assert.False(state.ReasoningOptions.Single(option => option.VariantId == "medium").IsAvailable);
        Assert.False(state.ShowSpeedOptions);
        await dispatcher.InvokeAsync(() => state.SelectedModel = state.Models[1]);
        var settings = AgentChatModelSettingsJson.Parse(state.SettingsJson);
        Assert.Equal("model", state.Selection.ModelId);
        Assert.Equal("fast", settings.SpeedOptionId);
        Assert.Equal("max", settings.ReasoningVariantId);
        Assert.False(state.HasWarning);
        await state.RefreshAsync(state.Selection, TestContext.Current.CancellationToken);
        Assert.Equal("Model · Fast", state.SelectedModel?.Label);
    }

    [Theory]
    [InlineData("{\"reasoningVariantId\":\"unknown\",\"speedOptionId\":\"fast\",\"modeOptionId\":\"pro\"}")]
    [InlineData("{\"reasoningVariantId\":\"medium\",\"speedOptionId\":\"retired\"}")]
    [InlineData("{\"reasoningVariantId\":\"none\",\"speedOptionId\":\"fast\"}")]
    [InlineData("{\"reasoningVariantId\":42}")]
    [InlineData("{\"futureSetting\":\"do-not-drop\"}")]
    [InlineData("null")]
    public async Task SavedSelections_ArePreservedForRepairIncludingCatalogFailures(string json)
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        var fail = false;
        var inner = SelectionCatalog();
        var catalog = new ProviderModelCatalogAdapter(token => inner.ListProvidersAsync(token), (id, token) =>
            fail ? throw new InvalidOperationException("offline") : inner.LoadAsync(id, token));
        using var state = new ModelBindingEditorState(new ProviderModelLoader(catalog), Options(), dispatcher);
        await state.RefreshAsync(new("provider", "model", json), TestContext.Current.CancellationToken);
        var saved = state.Selection;
        Assert.True(state.HasWarning);
        Assert.True(state.ShowModelSelection);
        if (!AgentChatModelSettingsJson.TryParse(json, out var parsed)) Assert.Equal(json, saved.SettingsJson);
        else Assert.Equal(parsed, AgentChatModelSettingsJson.Parse(saved.SettingsJson));

        fail = true;
        await state.RefreshAsync(saved, TestContext.Current.CancellationToken);

        Assert.Equal(saved, state.Selection);
        Assert.True(state.HasWarning);
        Assert.Contains("offline", state.WarningText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderListFailure_DoesNotReplaceSavedBindingWithAnUnrelatedDefault()
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        var catalog = new ProviderModelCatalogAdapter(_ => throw new InvalidOperationException("offline"),
            (_, _) => throw new InvalidOperationException("Must not load a provider after list failure"));
        using var state = new ModelBindingEditorState(new ProviderModelLoader(catalog), Options(), dispatcher);
        var saved = new ModelBindingSelection("missing", "model", "{\"reasoningVariantId\":\"max\"}");
        await state.RefreshAsync(saved, TestContext.Current.CancellationToken);
        Assert.Equal(saved.ProviderId, state.Selection.ProviderId);
        Assert.Equal(saved.ModelId, state.Selection.ModelId);
        Assert.Equal("max", AgentChatModelSettingsJson.Parse(state.SettingsJson).ReasoningVariantId);
        Assert.True(state.HasWarning);
        Assert.False(state.SelectedProvider?.IsAvailable);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NoNe")]
    public async Task RemovedNoneReasoningIsNeverAMenuRowAndRequiresAnExplicitReplacement(string id)
    {
        using var dispatcher = new DedicatedPresentationDispatcher();
        using var state = new ModelBindingEditorState(new ProviderModelLoader(SelectionCatalog()), Options(), dispatcher);
        await state.RefreshAsync(new("provider", "model", $"{{\"reasoningVariantId\":\"{id}\"}}"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(state.ReasoningOptions, option => string.Equals(option.VariantId, "none", StringComparison.OrdinalIgnoreCase));
        Assert.Null(state.SelectedReasoningOption);
        Assert.True(state.HasWarning);
        Assert.Contains("Choose Default", state.WarningText);
        await dispatcher.InvokeAsync(() => state.SelectedModel = state.Models[1]);
        Assert.Equal(id, AgentChatModelSettingsJson.Parse(state.SettingsJson).ReasoningVariantId);
        Assert.Equal("fast", AgentChatModelSettingsJson.Parse(state.SettingsJson).SpeedOptionId);
        await state.RefreshAsync(state.Selection, TestContext.Current.CancellationToken);
        Assert.Null(state.SelectedReasoningOption);
        await dispatcher.InvokeAsync(() => state.SelectedReasoningOption = state.ReasoningOptions[0]);
        Assert.False(state.HasWarning);
        Assert.Null(AgentChatModelSettingsJson.Parse(state.SettingsJson).ReasoningVariantId);
        Assert.Equal("fast", AgentChatModelSettingsJson.Parse(state.SettingsJson).SpeedOptionId);
    }

    private static IProviderModelCatalog SelectionCatalog() => new ProviderModelCatalogAdapter(
        _ => Task.FromResult<IReadOnlyList<ProviderCatalogOption>>([new("provider", "Provider")]),
        (_, _) => Task.FromResult(new ProviderModelCatalogResult([new("model", "Model",
            Variants: [new("none", "None"), new("low", "Low"), new("high", "High"), new("max", "Max")],
            SpeedOptions: [new("fast", "Fast")])], "Ready")));
}
