using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using static Sunder.Package.Agent.Mcp.Services.McpServerStackPayloadCodec;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed class McpServerStackContributor(
    McpServerCatalogService serverCatalog,
    IPackageContext packageContext) : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
{
    private const string PackageId = "sunder.package.agent.mcp";
    private const string SchemaId = "sunder.package.agent.mcp/server";
    private const string SecretPlaceholder = "Value not exported";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ContributorId => "sunder.package.agent.mcp.servers";

    public string DisplayName => "MCP Servers";

    public async ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        var servers = await serverCatalog.ListServersAsync(cancellationToken);
        var items = new List<StackExportItemDescriptor>();
        foreach (var server in servers.OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new StackExportItemDescriptor(
                server.ServerId,
                server.DisplayName,
                "mcp-server",
                Description: null,
                DefaultSelected: true,
                Details: await BuildExportDetailsAsync(server, cancellationToken)));
        }

        return items;
    }

    public async ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemSelections
            .Select(selection => selection.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var servers = (await serverCatalog.ListServersAsync(cancellationToken))
            .Where(server => selectedIds.Contains(server.ServerId))
            .ToArray();
        var fragments = new List<StackFragmentExport>();
        var warnings = new List<string>();
        foreach (var server in servers)
        {
            if (!request.IsDetailSelected(server.ServerId, DetailIds.Transport))
            {
                warnings.Add($"Skipped MCP server '{server.DisplayName}' because transport was not selected.");
                continue;
            }

            if (server.TransportType == ConfiguredMcpTransportType.Stdio && !request.IsDetailSelected(server.ServerId, DetailIds.LaunchCommand))
            {
                warnings.Add($"Skipped MCP server '{server.DisplayName}' because its launch command was not selected.");
                continue;
            }

            if (server.TransportType == ConfiguredMcpTransportType.HttpSse && !request.IsDetailSelected(server.ServerId, DetailIds.ServerUrl))
            {
                warnings.Add($"Skipped MCP server '{server.DisplayName}' because its server URL was not selected.");
                continue;
            }

            if (server.TransportType == ConfiguredMcpTransportType.Stdio)
            {
                warnings.Add($"MCP server '{server.DisplayName}' exports executable command arguments. Review them for embedded secrets before publishing the Stack.");
            }

            var payload = BuildPayload(
                server,
                request,
                warnings,
                await serverCatalog.GetHeadersAsync(server, cancellationToken),
                await serverCatalog.GetEnvironmentVariablesAsync(server, cancellationToken));
            if (payload is null)
            {
                continue;
            }

            var requiredInputs = BuildRequiredInputs(payload).ToArray();
            fragments.Add(new StackFragmentExport(
                FragmentId: "mcp-server." + SanitizeIdentifier(server.ServerId),
                SchemaId: SchemaId,
                SchemaVersion: 1,
                DisplayName: server.DisplayName,
                JsonPayload: JsonSerializer.Serialize(payload, JsonOptions),
                Description: payload.Description,
                DefaultSelected: true,
                RequiredInputs: requiredInputs,
                SourceItemId: server.ServerId));
        }

        return new StackExportContribution(
            fragments,
            fragments.Count == 0 ? [] : [CreatePackageRequirement()],
            warnings);
    }

    public async ValueTask<StackImportPreview> PreviewImportAsync(
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

            var existing = await serverCatalog.GetServerAsync(payload.ServerId, cancellationToken);
            actions.Add(new StackImportAction(
                BuildActionId(fragment.FragmentId, payload.ServerId),
                existing is null ? $"Create MCP server '{payload.DisplayName}'" : $"Update MCP server '{payload.DisplayName}'",
                existing is null ? StackImportActionKind.Create : StackImportActionKind.Update,
                DefaultSelected: true,
                Description: payload.Description));
            requiredInputs.AddRange(BuildRequiredInputs(payload));
        }

        if (requiredInputs.Count > 0)
        {
            warnings.Add("MCP Stack exports do not include raw secret values. Missing header and environment values must be supplied locally after import.");
        }

        return new StackImportPreview(actions, requiredInputs, [], warnings);
    }

    public async ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedActionIds = request.SelectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<StackImportedItem>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var selectedPayloads = new List<McpServerStackPayload>();
        foreach (var fragment in request.Fragments)
        {
            if (!TryReadPayload(fragment, warnings, out var payload) || payload is null)
            {
                continue;
            }

            var actionId = BuildActionId(fragment.FragmentId, payload.ServerId);
            if (!selectedActionIds.Contains(actionId))
            {
                continue;
            }

            selectedPayloads.Add(payload);
        }

        var duplicateIds = selectedPayloads
            .GroupBy(payload => payload.ServerId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var duplicateNames = selectedPayloads
            .GroupBy(payload => McpServerCatalogService.NormalizeName(payload.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0 || duplicateNames.Length > 0)
        {
            errors.Add("MCP Stack import contains duplicate or case-colliding server ids or names; no servers were changed.");
        }
        else
        {
            try
            {
                var writes = new List<McpServerCatalogWrite>();
                foreach (var payload in selectedPayloads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var existing = await serverCatalog.GetServerAsync(payload.ServerId, cancellationToken).ConfigureAwait(false);
                    var headers = existing is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(await serverCatalog.GetHeadersAsync(existing, cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
                    var environmentVariables = existing is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(await serverCatalog.GetEnvironmentVariablesAsync(existing, cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
                    var missingSecrets = ApplySecretInputs(payload.Headers, request.InputValues, headers)
                                         + ApplySecretInputs(payload.EnvironmentVariables, request.InputValues, environmentVariables);
                    var isEnabled = payload.IsEnabled && missingSecrets == 0;
                    if (payload.IsEnabled && missingSecrets > 0)
                    {
                        warnings.Add($"Imported MCP server '{payload.DisplayName}' disabled because {missingSecrets} secret value{(missingSecrets == 1 ? string.Empty : "s")} must be supplied locally.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    writes.Add(new McpServerCatalogWrite(
                        payload.ToServer(existing?.CreatedAtUtc ?? now, now, isEnabled),
                        headers,
                        environmentVariables));
                }

                cancellationToken.ThrowIfCancellationRequested();
                await serverCatalog.ApplyBatchAsync(writes, [], cancellationToken).ConfigureAwait(false);
                imported.AddRange(selectedPayloads.Select(payload => new StackImportedItem(payload.ServerId, payload.DisplayName, "mcp-server")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Failed to import MCP servers: {ex.Message}");
            }
        }

        var outcome = errors.Count == 0
            ? StackImportOutcome.Completed
            : imported.Count == 0 ? StackImportOutcome.Failed : StackImportOutcome.Partial;
        return new StackImportResult(outcome, imported, new Dictionary<string, string>(), warnings, errors);
    }

    public ValueTask OnStackImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ImportedItems.Count > 0)
        {
            serverCatalog.NotifyServersImported();
        }

        return ValueTask.CompletedTask;
    }

    private StackPackageRequirement CreatePackageRequirement()
        => new(PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.1.0");

    private async Task<IReadOnlyList<StackExportItemDetail>> BuildExportDetailsAsync(
        ConfiguredMcpServerRecord server,
        CancellationToken cancellationToken)
    {
        var details = new List<StackExportItemDetail>();
        if (!string.IsNullOrWhiteSpace(server.Description))
        {
            details.Add(new StackExportItemDetail(
                "Server description",
                server.Description.Trim(),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.Description));
        }

        details.Add(new StackExportItemDetail(
            "Transport",
            server.TransportType == ConfiguredMcpTransportType.HttpSse ? "HTTP/SSE server" : "Local command server",
            StackValueSensitivity.Public,
            DetailId: DetailIds.Transport,
            IsEditable: false));

        if (server.TransportType == ConfiguredMcpTransportType.HttpSse && !string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            details.Add(new StackExportItemDetail(
                "Server URL",
                server.EndpointUrl,
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.ServerUrl));
        }

        if (server.TransportType == ConfiguredMcpTransportType.HttpSse && server.OAuthEnabled)
        {
            details.Add(new StackExportItemDetail(
                "OAuth",
                server.OAuthScopes.Length == 0 ? "Enabled" : "Enabled: " + string.Join(' ', server.OAuthScopes),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.OAuth));
        }

        if (server.TransportType == ConfiguredMcpTransportType.Stdio && server.CommandParts.Length > 0)
        {
            details.Add(new StackExportItemDetail(
                "Launch command",
                string.Join(" ", server.CommandParts),
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.LaunchCommand));
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            details.Add(new StackExportItemDetail(
                "Working folder",
                server.WorkingDirectory,
                StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.WorkingFolder));
        }

        var headers = await serverCatalog.GetHeadersAsync(server, cancellationToken);
        foreach (var headerName in server.HeaderNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var isSecret = IsLikelySecretName(headerName);
            headers.TryGetValue(headerName, out var headerValue);
            details.Add(new StackExportItemDetail(
                "Header: " + headerName.Trim(),
                isSecret ? SecretPlaceholder : string.IsNullOrWhiteSpace(headerValue) ? headerName.Trim() : headerValue.Trim(),
                isSecret ? StackValueSensitivity.Secret : StackValueSensitivity.Public,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.Header(headerName),
                SupportsAskOnImport: true));
        }

        foreach (var variableName in server.EnvironmentVariableNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            details.Add(new StackExportItemDetail(
                "Environment: " + variableName.Trim(),
                SecretPlaceholder,
                StackValueSensitivity.Secret,
                ValueWhenExcluded: "Not exported",
                DetailId: DetailIds.Environment(variableName),
                SupportsAskOnImport: true));
        }

        return details;
    }

}

internal static class McpServerStackPayloadCodec
{
    private const string SecretPlaceholder = "Value not exported";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static McpServerStackPayload? BuildPayload(
        ConfiguredMcpServerRecord server,
        StackExportRequest request,
        ICollection<string> warnings,
        IReadOnlyDictionary<string, string> headerValues,
        IReadOnlyDictionary<string, string> environmentVariableValues)
    {
        var description = request.IsDetailSelected(server.ServerId, DetailIds.Description)
            ? request.GetDetailValue(server.ServerId, DetailIds.Description, server.Description ?? string.Empty)
            : null;
        var commandParts = Array.Empty<string>();
        if (server.TransportType == ConfiguredMcpTransportType.Stdio)
        {
            var command = request.GetDetailValue(server.ServerId, DetailIds.LaunchCommand, string.Join(" ", server.CommandParts));
            commandParts = SplitCommand(command);
            if (commandParts.Length == 0)
            {
                warnings.Add($"Skipped MCP server '{server.DisplayName}' because its launch command is empty.");
                return null;
            }
        }

        string? endpointUrl = null;
        if (server.TransportType == ConfiguredMcpTransportType.HttpSse)
        {
            endpointUrl = request.GetDetailValue(server.ServerId, DetailIds.ServerUrl, server.EndpointUrl ?? string.Empty);
            if (string.IsNullOrWhiteSpace(endpointUrl) || !Uri.TryCreate(endpointUrl, UriKind.Absolute, out _))
            {
                warnings.Add($"Skipped MCP server '{server.DisplayName}' because its server URL is empty or invalid.");
                return null;
            }
        }

        var headers = BuildSelectedReferences(
            server.ServerId,
            "header",
            server.HeaderNames,
            request,
            DetailIds.Header,
            headerValues,
            warnings,
            server.DisplayName);
        var environmentVariables = BuildSelectedReferences(
            server.ServerId,
            "environment",
            server.EnvironmentVariableNames,
            request,
            DetailIds.Environment,
            environmentVariableValues,
            warnings,
            server.DisplayName);

        return new McpServerStackPayload(
            server.ServerId,
            server.Name,
            server.DisplayName,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            server.IsEnabled,
            server.TransportType.ToString(),
            commandParts,
            request.IsDetailSelected(server.ServerId, DetailIds.WorkingFolder)
                ? request.GetDetailValue(server.ServerId, DetailIds.WorkingFolder, server.WorkingDirectory ?? string.Empty)
                : null,
            endpointUrl,
            server.TimeoutMilliseconds,
            server.DiscoveryTimeoutMilliseconds,
            server.ToolTimeoutMilliseconds,
            headers,
            environmentVariables)
        {
            OAuthEnabled = server.OAuthEnabled && request.IsDetailSelected(server.ServerId, DetailIds.OAuth),
            OAuthScopes = server.OAuthScopes,
            OAuthClientId = server.OAuthClientId,
        };
    }

    internal static IReadOnlyList<StackRequiredInputDescriptor> BuildRequiredInputs(McpServerStackPayload payload)
        => payload.Headers
            .Where(header => header.RequiresInput)
            .Select(header => new StackRequiredInputDescriptor(
                header.InputId,
                $"{payload.DisplayName} header: {header.Name}",
                Required: false,
                Description: "Header values are stored as local secrets and are not included in Stack exports."))
            .Concat(payload.EnvironmentVariables.Where(variable => variable.RequiresInput).Select(variable => new StackRequiredInputDescriptor(
                variable.InputId,
                $"{payload.DisplayName} environment: {variable.Name}",
                Required: false,
                Description: "Environment values are stored as local secrets and are not included in Stack exports.")))
            .ToArray();

    internal static int ApplySecretInputs(
        IReadOnlyList<McpStackSecretReference> references,
        IReadOnlyDictionary<string, string> inputValues,
        IDictionary<string, string> target)
    {
        var missing = 0;
        foreach (var reference in references)
        {
            if (!reference.RequiresInput)
            {
                if (!string.IsNullOrWhiteSpace(reference.Value))
                {
                    target[reference.Name] = reference.Value.Trim();
                }

                continue;
            }

            if (inputValues.TryGetValue(reference.InputId, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                target[reference.Name] = value.Trim();
                continue;
            }

            if (!target.ContainsKey(reference.Name))
            {
                missing++;
            }
        }

        return missing;
    }

    internal static bool TryReadPayload(StackFragmentImport fragment, ICollection<string> warnings, out McpServerStackPayload? payload)
    {
        payload = null;
        try
        {
            if (fragment.JsonPayload.Length > McpConfigurationSourceReader.MaxDocumentBytes)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' MCP server payload exceeds the size limit.");
                return false;
            }

            using var document = JsonDocument.Parse(fragment.JsonPayload, new JsonDocumentOptions
            {
                MaxDepth = McpConfigurationSourceReader.MaxJsonDepth,
            });
            McpJsonShapeValidator.RejectDuplicateProperties(document.RootElement);
            payload = document.RootElement.Deserialize<McpServerStackPayload>(JsonOptions);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.ServerId)
                || string.IsNullOrWhiteSpace(payload.Name)
                || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid MCP server payload.");
                payload = null;
                return false;
            }

            if (payload.ServerId.Length > 128
                || payload.Name.Length > 128
                || payload.DisplayName.Length > 256
                || payload.ServerId.Any(char.IsControl))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' MCP server identity exceeds its bounds or contains control characters.");
                payload = null;
                return false;
            }

            if (payload.Headers.Select(item => item.Name)
                    .Concat(payload.EnvironmentVariables.Select(item => item.Name))
                    .Any(string.IsNullOrWhiteSpace)
                || payload.Headers.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Headers.Count
                || payload.EnvironmentVariables.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.EnvironmentVariables.Count
                || payload.Headers.Select(item => item.InputId)
                    .Concat(payload.EnvironmentVariables.Select(item => item.InputId))
                    .Any(string.IsNullOrWhiteSpace)
                || payload.Headers.Select(item => item.InputId)
                    .Concat(payload.EnvironmentVariables.Select(item => item.InputId))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Headers.Count + payload.EnvironmentVariables.Count)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' MCP server payload contains empty, duplicate, or case-colliding secret names.");
                payload = null;
                return false;
            }

            if (!Enum.TryParse<ConfiguredMcpTransportType>(payload.TransportType, ignoreCase: false, out var transportType)
                || !Enum.IsDefined(transportType))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' MCP server payload has an unsupported transport.");
                payload = null;
                return false;
            }

            if (transportType == ConfiguredMcpTransportType.Stdio && payload.CommandParts.Count == 0)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' local MCP server payload is missing command parts.");
                payload = null;
                return false;
            }

            if (transportType == ConfiguredMcpTransportType.HttpSse
                && (string.IsNullOrWhiteSpace(payload.EndpointUrl) || !Uri.TryCreate(payload.EndpointUrl, UriKind.Absolute, out _)))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' remote MCP server payload is missing a valid URL.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            warnings.Add($"Stack fragment '{fragment.FragmentId}' MCP server payload could not be parsed: {ex.Message}");
            return false;
        }
    }

    internal static string BuildActionId(string fragmentId, string serverId)
        => $"mcp-server:{fragmentId}:{serverId}";

    internal static string SanitizeIdentifier(string value)
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
        return string.IsNullOrWhiteSpace(sanitized) ? "item" : sanitized;
    }

    internal sealed record McpServerStackPayload(
        string ServerId,
        string Name,
        string DisplayName,
        string? Description,
        bool IsEnabled,
        string TransportType,
        IReadOnlyList<string> CommandParts,
        string? WorkingDirectory,
        string? EndpointUrl,
        int? TimeoutMilliseconds,
        int? DiscoveryTimeoutMilliseconds,
        int? ToolTimeoutMilliseconds,
        IReadOnlyList<McpStackSecretReference> Headers,
        IReadOnlyList<McpStackSecretReference> EnvironmentVariables)
    {
        public bool OAuthEnabled { get; init; }

        public IReadOnlyList<string> OAuthScopes { get; init; } = [];

        public string? OAuthClientId { get; init; }

        public ConfiguredMcpServerRecord ToServer(DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, bool isEnabled)
            => new()
            {
                ServerId = ServerId,
                Name = Name,
                DisplayName = DisplayName,
                Description = Description,
                IsEnabled = isEnabled,
                TransportType = Enum.TryParse<ConfiguredMcpTransportType>(TransportType, ignoreCase: true, out var parsedTransport)
                    ? parsedTransport
                    : ConfiguredMcpTransportType.Stdio,
                CommandParts = CommandParts.ToArray(),
                WorkingDirectory = WorkingDirectory,
                EndpointUrl = EndpointUrl,
                TimeoutMilliseconds = TimeoutMilliseconds,
                DiscoveryTimeoutMilliseconds = DiscoveryTimeoutMilliseconds,
                ToolTimeoutMilliseconds = ToolTimeoutMilliseconds,
                HeaderNames = Headers.Select(header => header.Name).ToArray(),
                EnvironmentVariableNames = EnvironmentVariables.Select(variable => variable.Name).ToArray(),
                OAuthEnabled = OAuthEnabled,
                OAuthScopes = OAuthScopes.ToArray(),
                OAuthClientId = OAuthClientId,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
            };

        public static string BuildUniqueInputId(string serverId, string kind, string name, ISet<string> usedIds)
        {
            var baseId = $"mcp.{SanitizeIdentifier(serverId)}.{kind}.{SanitizeIdentifier(name)}";
            var inputId = baseId;
            var index = 2;
            while (!usedIds.Add(inputId))
            {
                inputId = baseId + "-" + index++;
            }

            return inputId;
        }
    }

    private static IReadOnlyList<McpStackSecretReference> BuildSelectedReferences(
        string serverId,
        string kind,
        IReadOnlyList<string> names,
        StackExportRequest request,
        Func<string, string> buildDetailId,
        IReadOnlyDictionary<string, string> values,
        ICollection<string> warnings,
        string serverDisplayName)
    {
        var references = new List<McpStackSecretReference>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var detailId = buildDetailId(name);
            if (!request.IsDetailSelected(serverId, detailId))
            {
                continue;
            }

            var fallbackSensitivity = IsLikelySecretName(name) ? StackValueSensitivity.Secret : StackValueSensitivity.Public;
            var sensitivity = request.GetDetailSensitivity(serverId, detailId, fallbackSensitivity);
            var inputId = McpServerStackPayload.BuildUniqueInputId(serverId, kind, name, usedIds);
            if (sensitivity == StackValueSensitivity.Secret)
            {
                references.Add(new McpStackSecretReference(name.Trim(), inputId, RequiresInput: true));
                continue;
            }

            var fallbackValue = fallbackSensitivity == StackValueSensitivity.Secret
                ? string.Empty
                : values.TryGetValue(name, out var storedValue) ? storedValue : string.Empty;
            var value = request.GetDetailValue(serverId, detailId, fallbackValue).Trim();
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, SecretPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped {kind} '{name}' for MCP server '{serverDisplayName}' because it was marked non-secret but has no exportable value.");
                continue;
            }

            references.Add(new McpStackSecretReference(name.Trim(), inputId, value, RequiresInput: false));
        }

        return references;
    }

    internal static bool IsLikelySecretName(string name)
        => name.Contains("key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || name.Contains("auth", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitCommand(string command)
        => command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static class DetailIds
    {
        public const string Description = "description";
        public const string Transport = "transport";
        public const string ServerUrl = "server-url";
        public const string LaunchCommand = "launch-command";
        public const string WorkingFolder = "working-folder";
        public const string OAuth = "oauth";

        public static string Header(string name) => "header." + SanitizeIdentifier(name);

        public static string Environment(string name) => "environment." + SanitizeIdentifier(name);
    }

    internal sealed record McpStackSecretReference(string Name, string InputId, string? Value = null, bool RequiresInput = true);
}
