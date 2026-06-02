using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Configuration;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Agent.Shared.Stacks;

internal sealed class PackageConfigurationStackContributor(
    PackageConfigurationSchema schema,
    IPackageContext packageContext) : IPackageStackContributor
{
    private const string SettingsItemId = "settings";
    private const string SecretPlaceholder = "Value not exported";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => schema.PackageId + ".settings";

    public string DisplayName => schema.PackageDisplayName + " Settings";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(
            [new StackExportItemDescriptor(
                SettingsItemId,
                schema.PackageDisplayName + " Settings",
                "package-settings",
                schema.Summary,
                DefaultSelected: false,
                Sensitivities: BuildExportItemSensitivities(),
                Details: BuildExportDetails())]);

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ItemIds.Contains(SettingsItemId, StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(new StackExportContribution([], [], []));
        }

        var hasExplicitDetails = request.GetItemSelection(SettingsItemId)?.Details is not null;
        var values = new List<PackageConfigurationStackValue>();
        var secretReferences = new List<PackageConfigurationStackSecretReference>();
        foreach (var field in ListFields())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsDetailSelected(SettingsItemId, field.Key))
            {
                continue;
            }

            if (field.Kind == PackageConfigurationFieldKind.Secret)
            {
                var sensitivity = request.GetDetailSensitivity(SettingsItemId, field.Key, StackValueSensitivity.Secret);
                if (sensitivity == StackValueSensitivity.Secret)
                {
                    if (!string.IsNullOrWhiteSpace(packageContext.Secrets.GetSecret(field.Key)))
                    {
                        secretReferences.Add(new PackageConfigurationStackSecretReference(
                            field.Key,
                            BuildInputId(field.Key),
                            field.Label,
                            field.Description,
                            field.IsRequired));
                    }

                    continue;
                }

                var overrideValue = request.GetDetailValue(SettingsItemId, field.Key, string.Empty);
                if (!string.IsNullOrWhiteSpace(overrideValue) && !string.Equals(overrideValue, SecretPlaceholder, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(new PackageConfigurationStackValue(field.Key, field.Label, field.Kind.ToString(), overrideValue));
                }

                continue;
            }

            var value = packageContext.Configuration.GetValue(field.Key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var sensitivity = request.GetDetailSensitivity(SettingsItemId, field.Key, StackValueSensitivity.Public);
                if (sensitivity == StackValueSensitivity.Secret)
                {
                    secretReferences.Add(new PackageConfigurationStackSecretReference(
                        field.Key,
                        BuildInputId(field.Key),
                        field.Label,
                        field.Description,
                        field.IsRequired));
                    continue;
                }

                value = request.GetDetailValue(SettingsItemId, field.Key, value);
                if (!hasExplicitDetails && (IsPathField(field) || IsLikelyLocalPath(field.Key, field.Label, value)) && !request.Options.IncludeMachineSpecificValues)
                {
                    continue;
                }

                if (!hasExplicitDetails && (IsNetworkField(field) || IsLikelyNetworkEndpoint(field.Key, field.Label, value)) && !request.Options.IncludeNetworkEndpoints)
                {
                    continue;
                }

                values.Add(new PackageConfigurationStackValue(field.Key, field.Label, field.Kind.ToString(), value));
            }
        }

        if (values.Count == 0 && secretReferences.Count == 0)
        {
            return ValueTask.FromResult(new StackExportContribution(
                [],
                [],
                [$"No configured settings were found for {schema.PackageDisplayName}."]));
        }

        var payload = new PackageConfigurationStackPayload(schema.PackageId, schema.PackageDisplayName, values, secretReferences);
        var fragment = new StackFragmentExport(
            FragmentId: "settings." + SanitizeIdentifier(schema.PackageId),
            OwnerPackageId: schema.PackageId,
            ContributorId,
            SchemaId: "sunder.package.configuration/settings",
            SchemaVersion: 1,
            DisplayName: schema.PackageDisplayName + " Settings",
            JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
            Safety: BuildSafety(values, secretReferences),
            Description: schema.Summary,
            DefaultSelected: true,
            RequiresPackages: [CreatePackageRequirement()],
            RequiredInputs: secretReferences.Select(ToRequiredInput).ToArray(),
            SourceItemId: SettingsItemId);

        return ValueTask.FromResult(new StackExportContribution([fragment], [CreatePackageRequirement()], []));
    }

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<StackImportAction>();
        var requiredInputs = new List<StackRequiredInputDescriptor>();
        var warnings = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId),
                $"Apply {payload.PackageDisplayName} settings",
                StackImportActionKind.Update,
                DefaultSelected: true,
                Description: schema.Summary));
            requiredInputs.AddRange(payload.SecretReferences.Select(ToRequiredInput));
        }

        if (requiredInputs.Count > 0)
        {
            warnings.Add("Package setting Stack exports do not include raw secret values. Missing secret values can be supplied locally before import.");
        }

        return ValueTask.FromResult(new StackImportPreview(actions, requiredInputs, [], warnings));
    }

    public async ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var fragment in request.Fragments)
        {
            if (!selectedActionIds.Contains(BuildActionId(fragment.FragmentId)))
            {
                continue;
            }

            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            try
            {
                foreach (var value in payload.Values)
                {
                    if (!TryGetField(value.Key, out var field))
                    {
                        continue;
                    }

                    if (field.Kind == PackageConfigurationFieldKind.Secret)
                    {
                        packageContext.Secrets.SetSecret(value.Key, value.Value);
                    }
                    else
                    {
                        await packageContext.Storage.State.SetValueAsync(value.Key, value.Value, cancellationToken);
                    }
                }

                foreach (var secretReference in payload.SecretReferences)
                {
                    if (!TryGetField(secretReference.Key, out var field))
                    {
                        continue;
                    }

                    if (request.InputValues.TryGetValue(secretReference.InputId, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        if (field.Kind == PackageConfigurationFieldKind.Secret)
                        {
                            packageContext.Secrets.SetSecret(secretReference.Key, value.Trim());
                        }
                        else
                        {
                            await packageContext.Storage.State.SetValueAsync(secretReference.Key, value.Trim(), cancellationToken);
                        }
                    }
                    else if (field.Kind == PackageConfigurationFieldKind.Secret && string.IsNullOrWhiteSpace(packageContext.Secrets.GetSecret(secretReference.Key)))
                    {
                        warnings.Add($"Secret setting '{secretReference.Label}' for {payload.PackageDisplayName} was not imported because no local value was provided.");
                    }
                    else if (field.Kind != PackageConfigurationFieldKind.Secret && string.IsNullOrWhiteSpace(await packageContext.Storage.State.GetValueAsync(secretReference.Key, cancellationToken)))
                    {
                        warnings.Add($"Setting '{secretReference.Label}' for {payload.PackageDisplayName} was not imported because no local value was provided.");
                    }
                }

                imported.Add(new StackImportedItem(payload.PackageId, payload.PackageDisplayName, "package-settings"));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import {payload.PackageDisplayName} settings: {ex.Message}");
            }
        }

        return new StackImportResult(errors.Count == 0, imported, new Dictionary<string, string>(), warnings, errors);
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(schema.PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private IReadOnlyList<StackValueSensitivity> BuildExportItemSensitivities()
    {
        var fields = ListFields().ToArray();
        var sensitivities = new List<StackValueSensitivity>();
        if (fields.Any(field => field.Kind == PackageConfigurationFieldKind.Secret))
        {
            sensitivities.Add(StackValueSensitivity.Secret);
        }

        if (fields.Any(field => IsPathField(field)))
        {
            sensitivities.Add(StackValueSensitivity.LocalPath);
            sensitivities.Add(StackValueSensitivity.MachineSpecific);
        }

        if (fields.Any(field => IsNetworkField(field)))
        {
            sensitivities.Add(StackValueSensitivity.NetworkEndpoint);
        }

        return sensitivities.Count == 0 ? [StackValueSensitivity.Public] : sensitivities.Distinct().ToArray();
    }

    private IReadOnlyList<StackExportItemDetail> BuildExportDetails()
    {
        var details = new List<StackExportItemDetail>();
        foreach (var field in ListFields())
        {
            if (field.Kind == PackageConfigurationFieldKind.Secret)
            {
                if (!string.IsNullOrWhiteSpace(packageContext.Secrets.GetSecret(field.Key)))
                {
                    details.Add(new StackExportItemDetail(
                        field.Label,
                        SecretPlaceholder,
                        StackValueSensitivity.Secret,
                        DetailId: field.Key,
                        SupportsAskOnImport: true));
                }

                continue;
            }

            var value = packageContext.Configuration.GetValue(field.Key);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var sensitivity = StackValueSensitivity.Public;
            if (IsPathField(field) || IsLikelyLocalPath(field.Key, field.Label, value))
            {
                sensitivity = StackValueSensitivity.LocalPath;
            }
            else if (IsNetworkField(field) || IsLikelyNetworkEndpoint(field.Key, field.Label, value))
            {
                sensitivity = StackValueSensitivity.NetworkEndpoint;
            }

            details.Add(new StackExportItemDetail(
                field.Label,
                value,
                sensitivity,
                ValueWhenExcluded: "Not exported",
                DetailId: field.Key,
                SupportsAskOnImport: true));
        }

        return details.Count == 0
            ? [new StackExportItemDetail("Package settings", "No configured values found yet", StackValueSensitivity.Public)]
            : details;
    }

    private static StackSafetyDescriptor BuildSafety(
        IReadOnlyList<PackageConfigurationStackValue> values,
        IReadOnlyList<PackageConfigurationStackSecretReference> secretReferences)
    {
        var hasLocalPaths = values.Any(value => IsLikelyLocalPath(value.Key, value.Label, value.Value));
        var hasNetworkEndpoints = values.Any(value => IsLikelyNetworkEndpoint(value.Key, value.Label, value.Value));
        return new StackSafetyDescriptor(
            ContainsSecrets: false,
            ContainsSecretReferences: secretReferences.Count > 0,
            ContainsLocalPaths: hasLocalPaths,
            ContainsPrivateText: false,
            ContainsExecutableCommands: false,
            ContainsNetworkEndpoints: hasNetworkEndpoints,
            ContainsMachineSpecificValues: hasLocalPaths);
    }

    private IEnumerable<PackageConfigurationField> ListFields()
        => schema.Sections.SelectMany(section => section.Fields);

    private bool TryGetField(string key, out PackageConfigurationField field)
    {
        field = ListFields().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))!;
        return field is not null;
    }

    private static StackRequiredInputDescriptor ToRequiredInput(PackageConfigurationStackSecretReference reference)
        => new(
            reference.InputId,
            StackRequiredInputKind.Secret,
            reference.Label,
            reference.Required,
            reference.Description ?? "Secret configuration values are stored locally and are not included in Stack exports.");

    private static bool TryReadPayload(
        StackFragmentImport fragment,
        ICollection<string> warnings,
        out PackageConfigurationStackPayload? payload)
    {
        payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<PackageConfigurationStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PackageId) || string.IsNullOrWhiteSpace(payload.PackageDisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid package settings payload.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' package settings payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    private static string BuildActionId(string fragmentId)
        => "package-settings:" + fragmentId;

    private static string BuildInputId(string key)
        => "settings." + SanitizeIdentifier(key);

    private static bool IsPathField(PackageConfigurationField field)
        => ContainsToken(field.Key, "path") || ContainsToken(field.Label, "path");

    private static bool IsNetworkField(PackageConfigurationField field)
        => ContainsToken(field.Key, "url")
           || ContainsToken(field.Label, "url")
           || ContainsToken(field.Key, "endpoint")
           || ContainsToken(field.Label, "endpoint");

    private static bool IsLikelyLocalPath(string key, string label, string value)
    {
        if (!ContainsToken(key, "path") && !ContainsToken(label, "path"))
        {
            return false;
        }

        return !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile;
    }

    private static bool IsLikelyNetworkEndpoint(string key, string label, string value)
        => (ContainsToken(key, "url") || ContainsToken(label, "url") || ContainsToken(key, "endpoint") || ContainsToken(label, "endpoint"))
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool ContainsToken(string value, string token)
        => value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string SanitizeIdentifier(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                pendingSeparator = false;
                continue;
            }

            if (builder.Length > 0 && !pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = true;
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "settings" : sanitized;
    }

    private sealed record PackageConfigurationStackPayload(
        string PackageId,
        string PackageDisplayName,
        IReadOnlyList<PackageConfigurationStackValue> Values,
        IReadOnlyList<PackageConfigurationStackSecretReference> SecretReferences);

    private sealed record PackageConfigurationStackValue(
        string Key,
        string Label,
        string Kind,
        string Value);

    private sealed record PackageConfigurationStackSecretReference(
        string Key,
        string InputId,
        string Label,
        string? Description,
        bool Required);
}
