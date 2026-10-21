using System.Collections.ObjectModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.PackageViews;

internal enum AgentEditorInvocationOperation
{
    Discovery,
    Refresh,
    Save,
}

internal enum AgentEditorInvocationFailureKind
{
    Runtime,
    Unavailable,
    OwnerFaulted,
}

internal sealed record AgentEditorInvocationFailure(
    string? PackageId,
    AgentEditorInvocationFailureKind Kind,
    string Message,
    string Code,
    int? StatusCode = null,
    string? CorrelationId = null)
{
    public string DiagnosticText
    {
        get
        {
            var parts = new List<string> { $"Code: {Code}" };
            if (StatusCode is not null)
            {
                parts.Add($"HTTP: {StatusCode.Value}");
            }
            if (CorrelationId is not null)
            {
                parts.Add($"Correlation: {CorrelationId}");
            }
            return string.Join(" | ", parts);
        }
    }

    public static AgentEditorInvocationFailure Runtime(
        string? packageId,
        AgentEditorInvocationOperation operation,
        SunderRpcException exception)
        => new(
            packageId,
            AgentEditorInvocationFailureKind.Runtime,
            operation == AgentEditorInvocationOperation.Save
                ? "Runtime could not save these workspace settings. Retry the affected section."
                : "Runtime could not load these workspace settings. Retry when Runtime is available.",
            exception.Error.Code);

    public static AgentEditorInvocationFailure Unavailable(
        string? packageId,
        AgentEditorInvocationOperation operation)
        => new(
            packageId,
            AgentEditorInvocationFailureKind.Unavailable,
            operation == AgentEditorInvocationOperation.Save
                ? "The package became unavailable before these workspace settings could be saved."
                : "The package providing these workspace settings is unavailable.",
            AgentToolResultErrorCodes.PackageUnavailable);

    public static AgentEditorInvocationFailure OwnerFaulted(string? packageId)
        => new(
            packageId,
            AgentEditorInvocationFailureKind.OwnerFaulted,
            "The package providing these workspace settings failed and was disabled.",
            "package-invariant-failure");
}

internal sealed record AgentEditorInvocationResult<TResult>(
    TResult Result,
    AgentEditorInvocationFailure? Failure)
{
    public bool Success => Failure is null;

    public static AgentEditorInvocationResult<TResult> Succeeded(TResult result) => new(result, null);

    public static AgentEditorInvocationResult<TResult> Failed(AgentEditorInvocationFailure failure)
        => new(default!, failure);
}

internal enum AgentEditorRetryKind
{
    Discovery,
    Refresh,
    Save,
}

internal sealed record AgentEditorRetryState(
    AgentEditorRetryKind Kind,
    AgentWorkspaceEditorIntent Intent,
    string? SectionId = null,
    AgentEditorSectionViewModel? OriginalSection = null);

internal static class AgentWorkspaceEditorInvocation
{
    public static async ValueTask<AgentEditorInvocationResult<IReadOnlyList<AgentEditorSection>>> ReadSectionsAsync(
        ServiceProvider<AgentServices.IWorkspaceEditorService> selected,
        AgentWorkspaceEditorContext context,
        AgentEditorInvocationOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var sections = await GetSectionsSnapshotAsync(selected.Service, context, cancellationToken).ConfigureAwait(false);
            await selected.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            return AgentEditorInvocationResult<IReadOnlyList<AgentEditorSection>>.Succeeded(sections);
        }
        catch (Exception exception)
        {
            return AgentEditorInvocationResult<IReadOnlyList<AgentEditorSection>>.Failed(
                await DescribeFailureAsync(selected, operation, exception, cancellationToken).ConfigureAwait(false));
        }
    }

    public static async ValueTask<AgentEditorInvocationResult<AgentEditorSaveResult>> SaveSectionAsync(
        ServiceProvider<AgentServices.IWorkspaceEditorService> selected,
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await selected.Service.SaveSectionAsync(context, request, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(result);
            await selected.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            return AgentEditorInvocationResult<AgentEditorSaveResult>.Succeeded(result);
        }
        catch (Exception exception)
        {
            return AgentEditorInvocationResult<AgentEditorSaveResult>.Failed(
                await DescribeFailureAsync(selected, AgentEditorInvocationOperation.Save, exception, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async ValueTask<AgentEditorInvocationFailure> DescribeFailureAsync(
        ServiceProvider<AgentServices.IWorkspaceEditorService> selected,
        AgentEditorInvocationOperation operation, Exception exception, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (exception is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable })
            return AgentEditorInvocationFailure.Unavailable(selected.PackageId, operation);
        if (exception is SunderRpcException rpc && rpc.Error.Kind != SunderRpcErrorKind.Validation)
            return AgentEditorInvocationFailure.Runtime(selected.PackageId, operation, rpc);
        if (selected.RetirementToken.IsCancellationRequested)
            return AgentEditorInvocationFailure.Unavailable(selected.PackageId, operation);
        return await selected.TryReportInvariantViolationAsync(exception, cancellationToken).ConfigureAwait(false)
            ? AgentEditorInvocationFailure.OwnerFaulted(selected.PackageId)
            : AgentEditorInvocationFailure.Unavailable(selected.PackageId, operation);
    }

    public static async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsSnapshotAsync(
        AgentServices.IWorkspaceEditorService contributor,
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken)
    {
        if (!await contributor.CanEditAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<AgentEditorSection>();
        }

        var sections = await contributor.GetSectionsAsync(context, cancellationToken).ConfigureAwait(false);
        return AgentEditorGraphSnapshot.Capture(sections);
    }
}

internal static class AgentEditorGraphSnapshot
{
    private const int MaxSections = 32;
    private const int MaxFieldsPerSection = 64;
    private const int MaxFields = 256;
    private const int MaxOptionsPerField = 256;
    private const int MaxOptions = 1024;
    private const int MaxItemsPerField = 256;
    private const int MaxItems = 1024;
    private const int MaxActionsPerField = 16;
    private const int MaxActions = 512;
    private const int MaxParametersPerAction = 32;
    private const int MaxStringLength = 8192;
    private const int MaxIdentifierLength = 256;
    private const int MaxTotalStringLength = 262_144;

    public static IReadOnlyList<AgentEditorSection> Capture(IReadOnlyList<AgentEditorSection>? sections)
    {
        var budget = new SnapshotBudget();
        var sectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return SnapshotList(sections, MaxSections, "sections", section =>
        {
            ArgumentNullException.ThrowIfNull(section);
            var sectionId = budget.RequiredIdentifier(section.SectionId, "section id");
            if (!sectionIds.Add(sectionId))
            {
                throw new InvalidDataException($"Editor contribution contains duplicate section id '{sectionId}'.");
            }

            var fieldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fields = SnapshotList(section.Fields, MaxFieldsPerSection, "section fields", field =>
            {
                budget.Consume(ref budget.Fields, MaxFields, "fields");
                ArgumentNullException.ThrowIfNull(field);
                var fieldId = budget.RequiredIdentifier(field.FieldId, "field id");
                if (!fieldIds.Add(fieldId))
                {
                    throw new InvalidDataException(
                        $"Editor section '{sectionId}' contains duplicate field id '{fieldId}'.");
                }

                var optionValues = new HashSet<string>(StringComparer.Ordinal);
                var options = SnapshotOptionalList(field.Options, MaxOptionsPerField, "field options", option =>
                {
                    budget.Consume(ref budget.Options, MaxOptions, "options");
                    ArgumentNullException.ThrowIfNull(option);
                    var value = budget.Required(option.Value, "option value");
                    if (!optionValues.Add(value))
                    {
                        throw new InvalidDataException(
                            $"Editor field '{fieldId}' contains duplicate option value '{value}'.");
                    }
                    return new AgentEditorOption(
                        value,
                        budget.Required(option.Label, "option label"),
                        budget.Optional(option.Description, "option description"));
                });

                var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var items = SnapshotOptionalList(field.Items, MaxItemsPerField, "field items", item =>
                {
                    budget.Consume(ref budget.Items, MaxItems, "items");
                    ArgumentNullException.ThrowIfNull(item);
                    var itemId = budget.RequiredIdentifier(item.ItemId, "item id");
                    if (!itemIds.Add(itemId))
                    {
                        throw new InvalidDataException(
                            $"Editor field '{fieldId}' contains duplicate item id '{itemId}'.");
                    }
                    return new AgentEditorListItem(
                        itemId,
                        budget.Required(item.Value, "item value", allowEmpty: true),
                        item.IsDefault)
                    {
                        SecondaryValue = budget.Optional(item.SecondaryValue, "item secondary value"),
                    };
                });

                var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var actions = SnapshotOptionalList(field.Actions, MaxActionsPerField, "field actions", action =>
                {
                    budget.Consume(ref budget.Actions, MaxActions, "actions");
                    ArgumentNullException.ThrowIfNull(action);
                    var actionId = budget.RequiredIdentifier(action.ActionId, "action id");
                    if (!actionIds.Add(actionId))
                    {
                        throw new InvalidDataException(
                            $"Editor field '{fieldId}' contains duplicate action id '{actionId}'.");
                    }
                    return new AgentEditorAction(
                        actionId,
                        budget.Required(action.Label, "action label"),
                        action.Kind,
                        budget.Optional(action.PackageId, "action package id"),
                        SnapshotParameters(action.Parameters, budget));
                });

                return new AgentEditorField(
                    fieldId,
                    budget.Required(field.Label, "field label"),
                    field.Kind,
                    budget.Optional(field.Description, "field description"),
                    budget.Optional(field.Value, "field value"),
                    options,
                    items,
                    budget.Optional(field.AddItemLabel, "field add-item label"),
                    field.UseFolderPicker,
                    budget.Optional(field.DefaultNewItemValue, "field default item value"))
                {
                    ItemValueLabel = budget.Optional(field.ItemValueLabel, "field item-value label"),
                    SecondaryItemValueLabel = budget.Optional(
                        field.SecondaryItemValueLabel,
                        "field secondary-item-value label"),
                    UseSecondaryFolderPicker = field.UseSecondaryFolderPicker,
                    DefaultNewSecondaryItemValue = budget.Optional(
                        field.DefaultNewSecondaryItemValue,
                        "field default secondary-item value"),
                    Actions = actions,
                };
            });

            return new AgentEditorSection(
                sectionId,
                budget.Required(section.Title, "section title"),
                budget.Optional(section.Description, "section description"),
                fields);
        });
    }

    private static IReadOnlyDictionary<string, string?>? SnapshotParameters(
        IReadOnlyDictionary<string, string?>? parameters,
        SnapshotBudget budget)
    {
        if (parameters is null)
        {
            return null;
        }
        if (parameters.Count > MaxParametersPerAction)
        {
            throw new InvalidDataException(
                $"Editor action parameters exceed the limit of {MaxParametersPerAction}.");
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (result.Count >= MaxParametersPerAction)
            {
                throw new InvalidDataException(
                    $"Editor action parameters exceed the limit of {MaxParametersPerAction}.");
            }
            result.Add(
                budget.RequiredIdentifier(parameter.Key, "action parameter name"),
                budget.Optional(parameter.Value, "action parameter value"));
        }
        return new ReadOnlyDictionary<string, string?>(result);
    }

    private static IReadOnlyList<TResult>? SnapshotOptionalList<TSource, TResult>(
        IReadOnlyList<TSource>? source,
        int limit,
        string name,
        Func<TSource, TResult> snapshot)
        => source is null ? null : SnapshotList(source, limit, name, snapshot);

    private static IReadOnlyList<TResult> SnapshotList<TSource, TResult>(
        IReadOnlyList<TSource>? source,
        int limit,
        string name,
        Func<TSource, TResult> snapshot)
    {
        if (source is null)
        {
            throw new InvalidDataException($"Editor contribution {name} cannot be null.");
        }

        var count = source.Count;
        if (count > limit)
        {
            throw new InvalidDataException($"Editor contribution {name} exceed the limit of {limit}.");
        }

        var result = new TResult[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = snapshot(source[index]);
        }
        return Array.AsReadOnly(result);
    }

    private sealed class SnapshotBudget
    {
        private int _stringLength;

        public int Fields;
        public int Options;
        public int Items;
        public int Actions;

        public void Consume(ref int value, int limit, string name)
        {
            value++;
            if (value > limit)
            {
                throw new InvalidDataException($"Editor contribution exceeds the total limit of {limit} {name}.");
            }
        }

        public string RequiredIdentifier(string? value, string name)
            => Required(value, name, MaxIdentifierLength);

        public string Required(
            string? value,
            string name,
            int maxLength = MaxStringLength,
            bool allowEmpty = false)
        {
            if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Editor contribution {name} is required.");
            }
            return Count(value, name, maxLength);
        }

        public string? Optional(string? value, string name)
            => value is null ? null : Count(value, name, MaxStringLength);

        private string Count(string value, string name, int maxLength)
        {
            if (value.Length > maxLength)
            {
                throw new InvalidDataException(
                    $"Editor contribution {name} exceeds the limit of {maxLength} characters.");
            }
            _stringLength = checked(_stringLength + value.Length);
            if (_stringLength > MaxTotalStringLength)
            {
                throw new InvalidDataException(
                    $"Editor contribution text exceeds the total limit of {MaxTotalStringLength} characters.");
            }
            return value;
        }
    }
}
