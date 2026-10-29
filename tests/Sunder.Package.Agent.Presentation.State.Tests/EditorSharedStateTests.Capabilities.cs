using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class EditorSharedStateTests
{
    [Fact]
    public void CapabilityBulkSelection_IsPackageOwnedAndConflictingDeclarationsDisableIt()
    {
        var state = new CapabilitySelectionState();
        var enabled = Definition("first");
        var ordinary = Definition("second") with { Group = enabled.Group with { AllowsBulkSelection = false } };
        state.Load([enabled, ordinary], []);
        var group = Assert.Single(state.Groups);
        Assert.False(group.AllowsBulkSelection);
        group.IsEnabled = true;
        Assert.Empty(state.Assignments);

        state.Reconcile([enabled, ordinary with { Group = enabled.Group }]);
        Assert.Same(group, Assert.Single(state.Groups));
        Assert.True(group.AllowsBulkSelection);
        group.IsEnabled = true;
        Assert.Equal(2, state.Assignments.Count);

        var omitted = new CapabilityGroupDefinition("servers", "Servers", null, 20);
        state.Load([ordinary with { Group = omitted }], []);
        group = Assert.Single(state.Groups);
        Assert.False(group.AllowsBulkSelection);
        group.IsEnabled = true;
        Assert.Empty(state.Assignments);
    }

    [Fact]
    public void CapabilityGrouping_PreservesPackageFlagsAndSeparatesSourcesWithTheSameGroupName()
    {
        var descriptor = new AgentToolDescriptor("read", "Read", "Read files", SourceId: "one", SelectionGroupId: "files");
        Assert.False(CapabilityGrouping.ForTool(descriptor).AllowsBulkSelection);
        Assert.True(CapabilityGrouping.ForTool(descriptor with { SelectionGroupAllowsBulkSelection = true }).AllowsBulkSelection);
        Assert.NotEqual(CapabilityGrouping.ForTool(descriptor).Key, CapabilityGrouping.ForTool(descriptor with { SourceId = "two" }).Key);
        var capability = new AgentProfileSelectableCapabilityDescriptor("tool-group", "server", "mcp", "Server", null);
        Assert.False(CapabilityGrouping.ForPackage(capability).AllowsBulkSelection);
        Assert.True(CapabilityGrouping.ForPackage(capability with { GroupAllowsBulkSelection = true }).AllowsBulkSelection);
    }

    [Fact]
    public void CapabilityReconciliation_RetainsGroupsAndOptionsWithoutResettingUnchangedCollections()
    {
        var state = new CapabilitySelectionState();
        var first = Definition("first");
        var second = Definition("second");
        state.Load([first, second], [new("tool", "first", "source")]);
        var group = Assert.Single(state.Groups);
        var option = state.Options[0];
        var collectionChanges = 0;
        state.Options.CollectionChanged += (_, _) => collectionChanges++;
        state.Groups.CollectionChanged += (_, _) => collectionChanges++;
        group.Options.CollectionChanged += (_, _) => collectionChanges++;
        var optionChanges = 0;
        option.PropertyChanged += (_, _) => optionChanges++;

        state.Reconcile([first with { }, second with { }]);

        Assert.Same(group, Assert.Single(state.Groups));
        Assert.Same(option, state.Options[0]);
        Assert.True(option.IsEnabled);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, optionChanges);

        state.Reconcile([first with { Description = "Updated description" }, second]);

        Assert.Same(group, Assert.Single(state.Groups));
        Assert.Same(option, state.Options[0]);
        Assert.Equal("Updated description", option.Description);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(1, optionChanges);
    }

    [Fact]
    public void CapabilityGroupToggle_BatchesChangesTracksMixedStateAndLeavesUnavailableAssignmentsAlone()
    {
        var state = new CapabilitySelectionState();
        var disabled = Definition("disabled") with { CanSelect = false };
        var other = Definition("other") with { Group = new("other", "Other", null, 20) };
        var unresolved = new AgentProfileSelectableCapabilityAssignmentRecord("tool", "missing", "source");
        var unavailable = new AgentProfileSelectableCapabilityAssignmentRecord("tool", "disabled", "source");
        state.Load([Definition("first"), Definition("second"), disabled, other],
            [new("tool", "first", "source"), unresolved, unavailable]);
        var group = state.Groups.Single(item => item.Title == "Tools");
        var changes = 0;
        state.Changed += () => changes++;
        Assert.Null(group.IsEnabled);

        group.IsEnabled = true;

        Assert.True(group.IsEnabled);
        Assert.All(group.Options.Where(option => option.CanSelect), option => Assert.True(option.IsEnabled));
        Assert.False(state.Options.Single(option => option.CapabilityId == "other").IsEnabled);
        Assert.False(state.Options.Single(option => option.CapabilityId == "disabled").IsEnabled);
        Assert.Equal(1, changes);

        group.Options.Single(option => option.CapabilityId == "first").IsEnabled = false;
        Assert.Null(group.IsEnabled);
        Assert.Equal(2, changes);
        group.IsEnabled = false;
        Assert.False(group.IsEnabled);
        Assert.Equal(3, changes);
        Assert.Equal([unresolved, unavailable], state.Assignments);

        state.Reconcile([disabled]);
        Assert.Same(group, Assert.Single(state.Groups));
        Assert.False(group.CanSelect);
        Assert.False(group.IsEnabled);
        group.IsEnabled = true;
        Assert.Equal(3, changes);
    }

    [Fact]
    public void RemovedCapabilityGroupsAndOptions_CannotMutateTheRetainedSelection()
    {
        var state = new CapabilitySelectionState();
        state.Load([Definition("first")], []);
        var group = Assert.Single(state.Groups);
        var option = Assert.Single(state.Options);
        var changes = 0;
        state.Changed += () => changes++;

        state.Clear();
        group.IsEnabled = true;
        option.IsEnabled = true;

        Assert.Empty(state.Assignments);
        Assert.Equal(0, changes);
    }
}
