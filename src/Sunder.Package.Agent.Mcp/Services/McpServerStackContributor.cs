using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using static Sunder.Package.Agent.Mcp.Services.McpServerStackPayloadCodec;

namespace Sunder.Package.Agent.Mcp.Services;

internal sealed class McpServerStackContributor(
    McpServerCatalogService serverCatalog,
    IPackageContext packageContext) : IPackageStackContributor, IPackageStackImportAppliedHandler
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
                Sensitivities: BuildSensitivities(server),
                Details: await BuildExportDetailsAsync(server, cancellationToken)));
        }

        return items;
    }

    public async ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = request.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                ContributorId,
                SchemaId,
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

            try
            {
                var existing = await serverCatalog.GetServerAsync(payload.ServerId, cancellationToken);
                var headers = existing is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(await serverCatalog.GetHeadersAsync(existing, cancellationToken), StringComparer.OrdinalIgnoreCase);
                var environmentVariables = existing is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(await serverCatalog.GetEnvironmentVariablesAsync(existing, cancellationToken), StringComparer.OrdinalIgnoreCase);
                var missingSecrets = ApplySecretInputs(payload.Headers, request.InputValues, headers)
                                     + ApplySecretInputs(payload.EnvironmentVariables, request.InputValues, environmentVariables);
                var isEnabled = payload.IsEnabled && missingSecrets == 0;
                if (payload.IsEnabled && missingSecrets > 0)
                {
                    warnings.Add($"Imported MCP server '{payload.DisplayName}' disabled because {missingSecrets} secret value{(missingSecrets == 1 ? string.Empty : "s")} must be supplied locally.");
                }

                var now = DateTimeOffset.UtcNow;
                var server = payload.ToServer(existing?.CreatedAtUtc ?? now, now, isEnabled);
                await serverCatalog.SaveServerAsync(server, headers, environmentVariables, cancellationToken);
                imported.Add(new StackImportedItem(payload.ServerId, payload.DisplayName, "mcp-server"));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to import MCP server '{payload.DisplayName}': {ex.Message}");
            }
        }

        return new StackImportResult(errors.Count == 0, imported, new Dictionary<string, string>(), warnings, errors);
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
        => new(PackageId, CreatedWithVersion: packageContext.Version.ToString(), MinimumVersion: "1.0.0");

    private static IReadOnlyList<StackValueSensitivity> BuildSensitivities(ConfiguredMcpServerRecord server)
    {
        var sensitivities = new List<StackValueSensitivity>();
        if (!string.IsNullOrWhiteSpace(server.Description))
        {
            sensitivities.Add(StackValueSensitivity.Public);
        }

        if (server.HeaderNames.Length > 0 || server.EnvironmentVariableNames.Length > 0)
        {
            sensitivities.Add(StackValueSensitivity.Secret);
        }

        if (server.TransportType == ConfiguredMcpTransportType.Stdio)
        {
            sensitivities.Add(StackValueSensitivity.Public);
        }

        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            sensitivities.Add(StackValueSensitivity.Public);
        }

        if (server.TransportType == ConfiguredMcpTransportType.HttpSse)
        {
            sensitivities.Add(StackValueSensitivity.Public);
        }

        if (server.OAuthEnabled)
        {
            sensitivities.Add(StackValueSensitivity.Public);
        }

        return sensitivities.Count == 0 ? [StackValueSensitivity.Public] : sensitivities.Distinct().ToArray();
    }

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
            payload = JsonSerializer.Deserialize<McpServerStackPayload>(fragment.JsonPayload, JsonOptions);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.ServerId)
                || string.IsNullOrWhiteSpace(payload.Name)
                || string.IsNullOrWhiteSpace(payload.DisplayName))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' does not contain a valid MCP server payload.");
                payload = null;
                return false;
            }

            if (payload.TransportType == nameof(ConfiguredMcpTransportType.Stdio) && payload.CommandParts.Count == 0)
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' local MCP server payload is missing command parts.");
                payload = null;
                return false;
            }

            if (payload.TransportType == nameof(ConfiguredMcpTransportType.HttpSse)
                && (string.IsNullOrWhiteSpace(payload.EndpointUrl) || !Uri.TryCreate(payload.EndpointUrl, UriKind.Absolute, out _)))
            {
                warnings.Add($"Stack fragment '{fragment.FragmentId}' remote MCP server payload is missing a valid URL.");
                payload = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
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
