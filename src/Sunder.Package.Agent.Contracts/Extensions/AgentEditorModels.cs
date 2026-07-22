namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the presentation and value shape of a package-contributed editor field.
/// </summary>
/// <remarks>
/// Field kinds are App presentation hints shared across the App/Runtime boundary. They do not validate values,
/// authorize filesystem access, or determine how a Runtime contributor persists data. Consumers must tolerate
/// future enum values and use a safe fallback presentation.
/// </remarks>
public enum AgentEditorFieldKind
{
    /// <summary>
    /// A single free-form string value edited as text.
    /// </summary>
    Text = 0,

    /// <summary>
    /// A single opaque value selected from the field's ordered options.
    /// </summary>
    Select = 1,

    /// <summary>
    /// An ordered collection of string items with an optional secondary value and one preferred item.
    /// </summary>
    /// <remarks>Folder-picker flags are convenience UI only; selected paths remain untrusted input.</remarks>
    PathList = 2,
}

/// <summary>
/// Identifies an App action attached to a package-contributed editor field.
/// </summary>
/// <remarks>
/// Actions request host UI behavior and do not directly execute package code or confer authorization. Action
/// identifiers, package identifiers, and parameters are package-supplied input and must be validated by the App.
/// Consumers must tolerate future enum values by declining unsupported actions.
/// </remarks>
public enum AgentEditorActionKind
{
    /// <summary>
    /// Requests navigation to the settings UI for the action's package identifier.
    /// </summary>
    /// <remarks>Navigation availability is host-dependent, and parameters must not carry credentials or secrets.</remarks>
    OpenPackageSettings = 0,

    /// <summary>
    /// Requests a fresh section snapshot and reapplies the matching field descriptor.
    /// </summary>
    /// <remarks>
    /// Refresh is an invalidation operation, not a save. Matching uses section and field identities, and failure can
    /// surface as an editor error without changing persisted Runtime configuration.
    /// </remarks>
    RefreshField = 2,
}

/// <summary>
/// Describes one ordered section supplied by an App or Runtime workspace-editor contributor.
/// </summary>
/// <remarks>
/// Section identity is scoped to its contributor. The App preserves contributor and field-list order and routes a
/// save back to the contributor that produced the section. The record and its field list are transport snapshots;
/// producers must not mutate them after return. All labels and descriptions are package-supplied display text and
/// must not contain secrets or be interpreted as executable markup.
/// </remarks>
/// <param name="SectionId">
/// The stable, non-empty identifier used to route saves and refreshes within the contributor. It should be unique
/// under ordinal case-insensitive comparison.
/// </param>
/// <param name="Title">The non-empty, non-sensitive heading displayed for the section.</param>
/// <param name="Description">Optional explanatory display text, or <see langword="null"/>.</param>
/// <param name="Fields">
/// The read-only ordered field descriptors in the section. Field identifiers must be unique within the section.
/// </param>
public sealed record AgentEditorSection(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<AgentEditorField> Fields);

/// <summary>
/// Describes one editable value and its App presentation hints.
/// </summary>
/// <remarks>
/// A field is a transport snapshot, not a validation schema. Values returned in
/// <see cref="AgentEditorSaveRequest"/> originate from the App and must be treated as untrusted by the Runtime
/// contributor, even when a picker or option list produced them. Producers must not mutate referenced option,
/// item, or action collections after returning the descriptor.
/// </remarks>
/// <param name="FieldId">
/// The stable, non-empty identifier used as the save-request dictionary key and to match refreshed fields. It
/// should be unique within the section under ordinal case-insensitive comparison.
/// </param>
/// <param name="Label">The non-empty, non-sensitive label displayed beside the field.</param>
/// <param name="Kind">The field's value shape and preferred App presentation.</param>
/// <param name="Description">Optional explanatory display text, or <see langword="null"/>.</param>
/// <param name="Value">
/// The current scalar value for <see cref="AgentEditorFieldKind.Text"/> or
/// <see cref="AgentEditorFieldKind.Select"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="Options">
/// The ordered choices for a select field, or <see langword="null"/> for other kinds. Values should be unique
/// under the contributor's matching rules.
/// </param>
/// <param name="Items">
/// The ordered current items for a path-list field, or <see langword="null"/> for scalar fields.
/// </param>
/// <param name="AddItemLabel">
/// Optional text for the path-list add command. The App uses a generic label when this value is blank.
/// </param>
/// <param name="UseFolderPicker">
/// Whether the App should offer a folder picker for the primary path-list value. This is not proof that a selected
/// path is valid, in scope, or authorized.
/// </param>
/// <param name="DefaultNewItemValue">
/// Optional initial primary value for a newly added path-list item. It is a convenience default and must still be
/// validated when saved.
/// </param>
public sealed record AgentEditorField(
    string FieldId,
    string Label,
    AgentEditorFieldKind Kind,
    string? Description = null,
    string? Value = null,
    IReadOnlyList<AgentEditorOption>? Options = null,
    IReadOnlyList<AgentEditorListItem>? Items = null,
    string? AddItemLabel = null,
    bool UseFolderPicker = false,
    string? DefaultNewItemValue = null)
{
    /// <summary>
    /// Gets optional text labeling the primary value within each path-list item.
    /// </summary>
    /// <remarks>The value is display text only and must not contain sensitive data.</remarks>
    public string? ItemValueLabel { get; init; }

    /// <summary>
    /// Gets optional text labeling a secondary value within each path-list item.
    /// </summary>
    /// <remarks>A non-empty label enables secondary-value presentation; it does not define validation.</remarks>
    public string? SecondaryItemValueLabel { get; init; }

    /// <summary>
    /// Gets whether the App should offer a folder picker for the secondary item value.
    /// </summary>
    /// <remarks>This hint does not establish path existence, scope, or permission.</remarks>
    public bool UseSecondaryFolderPicker { get; init; }

    /// <summary>
    /// Gets the optional initial secondary value for a newly added path-list item.
    /// </summary>
    /// <remarks>The value is untrusted on save and must not contain embedded credentials.</remarks>
    public string? DefaultNewSecondaryItemValue { get; init; }

    /// <summary>
    /// Gets optional ordered App actions associated with the field.
    /// </summary>
    /// <remarks>
    /// Actions are presentation requests rather than Runtime commands. Producers must not mutate the list after
    /// return, and the App may omit actions it cannot safely support.
    /// </remarks>
    public IReadOnlyList<AgentEditorAction>? Actions { get; init; }
}

/// <summary>
/// Describes one choice in a select editor field.
/// </summary>
/// <remarks>
/// Options are ordered presentation snapshots. Option values are returned to the contributor unchanged but are
/// not trusted or authorized merely because they appeared in a contributed list.
/// </remarks>
/// <param name="Value">The stable opaque value submitted when this option is selected.</param>
/// <param name="Label">The non-sensitive human-readable option label.</param>
/// <param name="Description">Optional explanatory display text, or <see langword="null"/>.</param>
public sealed record AgentEditorOption(string Value, string Label, string? Description = null);

/// <summary>
/// Represents one ordered value in a path-list editor field.
/// </summary>
/// <remarks>
/// Item identifiers are scoped to one field snapshot and can be regenerated by an App during editing; contributors
/// must not use them as durable authorization identities. Primary and secondary values are untrusted App input and
/// require validation, normalization, and scope checks before persistence or filesystem use.
/// </remarks>
/// <param name="ItemId">The non-empty item identity within the current field snapshot.</param>
/// <param name="Value">The primary string or path value.</param>
/// <param name="IsDefault">
/// Whether this is the preferred item. Contributors should tolerate malformed input with zero or multiple defaults
/// and normalize it according to package policy.
/// </param>
public sealed record AgentEditorListItem(string ItemId, string Value, bool IsDefault = false)
{
    /// <summary>
    /// Gets an optional secondary string or path value associated with the item.
    /// </summary>
    /// <remarks>The value is untrusted and has no semantics unless the producing field defines a secondary label.</remarks>
    public string? SecondaryValue { get; init; }
}

/// <summary>
/// Describes one host-handled action displayed with an editor field.
/// </summary>
/// <remarks>
/// Actions cross a package-to-App trust boundary. They are declarative UI requests, are not executed automatically,
/// and do not authorize navigation, package operations, filesystem access, or Runtime changes. The App may reject
/// unsupported or invalid actions. Referenced parameter dictionaries are snapshots and must not be mutated.
/// </remarks>
/// <param name="ActionId">
/// The stable action identity within the field, used for diagnostics and UI identity rather than authorization.
/// </param>
/// <param name="Label">The non-sensitive human-readable action label.</param>
/// <param name="Kind">The host behavior requested by the action.</param>
/// <param name="PackageId">
/// The canonical target package id when required by the action kind, or <see langword="null"/> otherwise. The App
/// must validate the id and target availability.
/// </param>
/// <param name="Parameters">
/// Optional action-specific string parameters forwarded to the supporting App service. Values must not contain
/// secrets and must be validated by the receiver.
/// </param>
public sealed record AgentEditorAction(
    string ActionId,
    string Label,
    AgentEditorActionKind Kind,
    string? PackageId = null,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>
/// Carries the App-edited value of one field back to its Runtime or App contributor.
/// </summary>
/// <remarks>
/// This transport value is untrusted regardless of its originating control. A scalar field normally supplies
/// <see cref="Value"/>, while a path-list field supplies <see cref="Items"/>; contributors must reject or normalize
/// inconsistent shapes, unknown options, invalid paths, duplicates, and unauthorized values.
/// </remarks>
/// <param name="Value">The edited scalar value, or <see langword="null"/> for a list-valued field.</param>
/// <param name="Items">
/// The edited ordered item list, or <see langword="null"/> for a scalar field. The contributor must treat the list
/// as read-only and validate every item.
/// </param>
public sealed record AgentEditorFieldValue(
    string? Value = null,
    IReadOnlyList<AgentEditorListItem>? Items = null);

/// <summary>
/// Carries all current field values for one contributed editor section to its owning contributor.
/// </summary>
/// <remarks>
/// The App routes the request by the section and contributor that produced the UI, but neither route establishes
/// authorization. Runtime contributors must verify that the section is theirs, validate expected and unknown field
/// keys, treat all values as untrusted, and perform persistence atomically where possible. The dictionary is a
/// read-only call snapshot and key enumeration order has no contract meaning.
/// </remarks>
/// <param name="SectionId">
/// The stable section identifier being saved, matched according to the contributor's documented comparison rules.
/// </param>
/// <param name="Fields">
/// The field-id-to-edited-value snapshot. Keys can be missing, unknown, or differently cased and values can be
/// malformed; contributors must not rely on UI-side validation.
/// </param>
public sealed record AgentEditorSaveRequest(
    string SectionId,
    IReadOnlyDictionary<string, AgentEditorFieldValue> Fields);

/// <summary>
/// Reports the outcome of saving one contributed editor section.
/// </summary>
/// <remarks>
/// The result controls App flow and display only; it is not a durable receipt or security audit record. A failure
/// stops the current multi-section save sequence, while exceptions can surface as editor errors. Messages can be
/// shown to users and therefore must be concise, safe for display, and free of secrets, raw credentials, or
/// unnecessary exception details.
/// </remarks>
/// <param name="Success">
/// Whether the contributor accepted and persisted the section values. Contributors should return
/// <see langword="false"/> for expected validation failures and reserve exceptions for unexpected failures.
/// </param>
/// <param name="Message">The non-sensitive human-readable outcome message.</param>
public sealed record AgentEditorSaveResult(
    bool Success,
    string Message)
{
    /// <summary>
    /// Creates a successful section-save result.
    /// </summary>
    /// <param name="message">The non-sensitive message suitable for display to the user.</param>
    /// <returns>A result whose <see cref="Success"/> value is <see langword="true"/>.</returns>
    public static AgentEditorSaveResult Ok(string message) => new(true, message);

    /// <summary>
    /// Creates an unsuccessful section-save result for an expected validation or persistence outcome.
    /// </summary>
    /// <param name="message">
    /// The actionable, non-sensitive failure message suitable for display to the user.
    /// </param>
    /// <returns>A result whose <see cref="Success"/> value is <see langword="false"/>.</returns>
    public static AgentEditorSaveResult Failed(string message) => new(false, message);
}
