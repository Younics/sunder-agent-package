using System.Text;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using IPromptContextContributorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Prompt_Context_Contributor.IPromptContextContributorService;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using ISelectableCapabilityProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Selectable_Capability_Provider.ISelectableCapabilityProviderService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Skills.Services;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "skills.tools")]
[SunderServiceProvider(typeof(IPromptContextContributorService), ProviderId = "skills.prompt.context")]
[SunderServiceProvider(typeof(ISelectableCapabilityProviderService), ProviderId = "skills.selectable.capabilities")]
public sealed partial class SkillsFeature(SkillStore store, IServiceProviders<IRuntimeCatalogService> catalogs)
    : IToolSourceService, IPromptContextContributorService, ISelectableCapabilityProviderService
{
    private const int MaxSkillToolChars = 60000;
    private const int DefaultReadLimit = 2000;
    private const int MaxReadLimit = 5000;
    private static readonly AgentToolActivationRequirement ActivationRequirement = new()
    { CapabilityKind = SkillConstants.CapabilityKind, SourceId = SkillConstants.PackageId, CapabilityId = null };

    private static readonly AgentToolDescriptor[] ToolDescriptors =
    [
        new()
        {
            ToolId = SkillConstants.SkillToolId, DisplayName = "Load Skill", Description = "Load full SKILL.md instructions for an enabled skill.",
            IsReadOnly = true, RequiresNetwork = false,
            ArgumentsJsonSchema = """
            {"type":"object","properties":{"name":{"type":"string","description":"Enabled skill name or id."}},"required":["name"],"additionalProperties":false}
            """,
            SourceKind = "skills", SourceId = SkillConstants.PackageId, SourceDisplayName = "Agent Skills",
            SelectionScope = AgentToolSelectionScope.Group, SelectionGroupId = SkillConstants.SkillToolGroupId,
            SelectionGroupDisplayName = "Agent Skills", SelectionGroupDescription = "Load enabled Agent Skills and read their bundled resources.",
            RuntimeInstructions = "Use `skill` only after the current task matches an enabled skill from the Skills prompt block.",
            ActivationRequirement = ActivationRequirement, Aliases = null, Priority = AgentToolPriority.Medium, ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
        },
        new()
        {
            ToolId = SkillConstants.SkillResourceToolId, DisplayName = "Read Skill Resource", Description = "Read or list files bundled inside an enabled skill folder.",
            IsReadOnly = true, RequiresNetwork = false,
            ArgumentsJsonSchema = """
            {"type":"object","properties":{"skill":{"type":"string","description":"Enabled skill name or id."},"path":{"type":"string","description":"Relative path inside the skill folder. Omit or use empty string to list the root."},"offset":{"type":"integer","description":"1-indexed line offset for text files."},"limit":{"type":"integer","description":"Maximum lines to return for text files."}},"required":["skill"],"additionalProperties":false}
            """,
            SourceKind = "skills", SourceId = SkillConstants.PackageId, SourceDisplayName = "Agent Skills",
            SelectionScope = AgentToolSelectionScope.Group, SelectionGroupId = SkillConstants.SkillToolGroupId,
            SelectionGroupDisplayName = "Agent Skills", SelectionGroupDescription = "Load enabled Agent Skills and read their bundled resources.",
            RuntimeInstructions = "Use `skill_resource` for references/assets after loading a relevant skill. Paths are relative to that skill folder.",
            ActivationRequirement = ActivationRequirement, Aliases = null, Priority = AgentToolPriority.Medium, ConcurrencyMode = AgentToolConcurrencyMode.Sequential,
        },
    ];

    public string ProviderId => SkillConstants.PackageId;

    public string ContributorId => SkillConstants.PackageId;

    public string SourceId => SkillConstants.PackageId;

    public string DisplayName => "Agent Skills";

    public string SourceKind => "skills";

    public event Action? SelectableCapabilitiesChanged
    {
        add => store.SkillsChanged += value;
        remove => store.SkillsChanged -= value;
    }

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentToolSourceDescriptor { SourceId = SourceId, DisplayName = DisplayName, SourceKind = SourceKind }); }

    ValueTask<AgentPromptContextContributorDescriptor> IPromptContextContributorService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentPromptContextContributorDescriptor { ContributorId = ContributorId, DisplayName = DisplayName, AcknowledgmentSinkId = null }); }

    ValueTask<AgentServiceIdentity> ISelectableCapabilityProviderService.DescribeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = ProviderId, DisplayName = DisplayName }); }

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(
        AgentProfileRecord? profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(store.ListSkills()
            .Select(skill => new AgentProfileSelectableCapabilityDescriptor
            {
                Kind = SkillConstants.CapabilityKind, CapabilityId = skill.SkillId, SourceId = SkillConstants.PackageId,
                DisplayName = SkillStore.ResolveDisplayName(skill), Description = skill.Description, StatusText = BuildStatusText(skill),
                IsSelectable = true, SourceDisplayName = DisplayName, GroupId = SkillConstants.SkillToolGroupId,
                GroupDisplayName = DisplayName, GroupDescription = "Reusable skills exposed to the agent.", GroupSortOrder = 30,
            })
            .ToArray());
    }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>(ToolDescriptors);
    }

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (toolId, context) = (request.ToolId, request.Context);
        if (!IsSkillTool(toolId))
        {
            return ValueTask.FromResult<AgentToolReadiness?>(null);
        }

        var enabled = HasEnabledSkills(context.Profile);
        return ValueTask.FromResult<AgentToolReadiness?>(new AgentToolReadiness { ToolId = toolId,
            Status = enabled ? AgentToolReadinessStatus.Ready : AgentToolReadinessStatus.Failed,
            Message = enabled ? "Enabled skill resources are ready." : "Enable at least one skill on this profile." });
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        var profile = await GetProfileAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Error(request.ToolId, "Skill tools require an active profile.", "skills-profile-required");
        }

        try
        {
            return request.ToolId.ToLowerInvariant() switch
            {
                SkillConstants.SkillToolId => await ExecuteSkillAsync(profile, context, request, context.ExecutionTarget, cancellationToken),
                SkillConstants.SkillResourceToolId => await ExecuteSkillResourceAsync(profile, context, request, context.ExecutionTarget, cancellationToken),
                _ => Error(request.ToolId, $"Unknown skill tool '{request.ToolId}'.", "skills-tool-unknown"),
            };
        }
        catch (SunderRpcException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(request.ToolId, ex.Message, "skills-execution");
        }
    }

    public async ValueTask<AgentPromptContextContribution?> ContributeAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.Profile ?? await GetProfileAsync(request.Session.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var enabledSkills = ListEnabledSkills(profile).ToArray();
        if (enabledSkills.Length == 0)
        {
            return null;
        }

        var resolvedResources = await ResolveResourcesAsync(
            profile,
            request.Session.SessionId,
            request.Workspace,
            request.ExecutionBinding,
            request.ExecutionTarget,
            cancellationToken);
        var pathsBySkillId = resolvedResources.ToDictionary(resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase);

        var content = new StringBuilder();
        content.AppendLine("This profile has skills available. Use the `skill` tool when the current task matches a skill. Use `skill_resource` to read extra files bundled with an enabled skill.")
            .AppendLine()
            .AppendLine("Enabled skills:");
        foreach (var skill in enabledSkills)
        {
            content.Append("- ").Append(SkillStore.ResolveDisplayName(skill));
            if (!string.IsNullOrWhiteSpace(skill.Description))
            {
                content.Append(": ").Append(skill.Description.Trim());
            }

            content.AppendLine();
            if (pathsBySkillId.TryGetValue(skill.SkillId, out var resource))
            {
                content.Append("  Executor path: ").AppendLine(resource.ExecutionPath);
            }
        }

        if (resolvedResources.Count == 0)
        {
            content.AppendLine()
                .AppendLine("Skill folders are installed locally, but the selected execution target did not expose executor paths for this session. Use `skill` and `skill_resource` for read-only skill content.");
        }
        else
        {
            content.AppendLine()
                .AppendLine("Executor paths are read-only skill folders. They are useful when a loaded skill asks you to pass bundled scripts or assets to shell commands. Host paths are not valid inside Docker containers.");
        }

        return new AgentPromptContextContribution
        {
            Blocks = [new()
            {
                Title = "Skills", Content = content.ToString().Trim(), Priority = 80, SourceId = SkillConstants.PackageId,
                Provenance = AgentContextProvenance.Extension, Trust = AgentContextTrust.Untrusted,
                Usage = AgentPromptContextUsage.Reference, Scope = null, Authority = AgentPromptContextAuthority.Reference, HostIdentity = null,
            }],
        };
    }

    private IReadOnlyList<AgentExecutionResourceDescriptor> ListResources(AgentProfileRecord profile)
        => ListEnabledSkills(profile)
            .Select(skill => new AgentExecutionResourceDescriptor
            {
                ResourceId = skill.SkillId, ResourceKind = SkillConstants.CapabilityKind, SourceId = SkillConstants.PackageId,
                DisplayName = SkillStore.ResolveDisplayName(skill), HostPath = store.GetSkillRootPath(skill),
                PreferredExecutionPath = SkillConstants.PreferredExecutionRoot + "/" + skill.SkillId,
                AccessMode = AgentExecutionResourceAccessMode.ReadOnly,
                Metadata = new Dictionary<string, string>
                {
                    ["skill_id"] = skill.SkillId,
                    ["name"] = skill.Name ?? string.Empty,
                },
            })
            .ToArray();

    private async Task<AgentToolResult> ExecuteSkillAsync(
        AgentProfileRecord profile,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        ServiceReference<IExecutionTargetService>? selectedTarget,
        CancellationToken cancellationToken)
    {
        if (!TryParseSkillArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "skills-arguments-invalid");
        }

        var skill = ResolveEnabledSkill(profile, args.Name);
        if (skill is null)
        {
            return Error(request.ToolId, $"Skill '{args.Name}' is not enabled for this profile.", "skill-not-enabled");
        }

        var parsed = SkillMarkdownParser.Parse(store.ReadSkillMarkdown(skill));
        var resources = await ResolveResourcesAsync(
            profile,
            context.SessionId,
            context.Workspace,
            context.ExecutionBinding,
            selectedTarget,
            cancellationToken);
        var resource = resources.FirstOrDefault(item => string.Equals(item.ResourceId, skill.SkillId, StringComparison.OrdinalIgnoreCase));
        var content = BuildSkillContent(skill, parsed, resource);
        return Result(request.ToolId, $"Loaded skill '{SkillStore.ResolveDisplayName(skill)}'.",
            Truncate(content, MaxSkillToolChars), content.Length > MaxSkillToolChars);
    }

    private async Task<AgentToolResult> ExecuteSkillResourceAsync(
        AgentProfileRecord profile,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        ServiceReference<IExecutionTargetService>? selectedTarget,
        CancellationToken cancellationToken)
    {
        if (!TryParseSkillResourceArgs(request.ArgumentsJson, out var args, out var error))
        {
            return Error(request.ToolId, error!, "skills-arguments-invalid");
        }

        var skill = ResolveEnabledSkill(profile, args.Skill);
        if (skill is null)
        {
            return Error(request.ToolId, $"Skill '{args.Skill}' is not enabled for this profile.", "skill-not-enabled");
        }

        var root = store.GetSkillRootPath(skill);
        var path = ResolveSkillResourcePath(root, args.Path);
        if (Directory.Exists(path))
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) + "/" : Path.GetFileName(entry))
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
            return Result(request.ToolId, $"Listed skill resource directory '{NormalizeDisplayPath(args.Path)}'.", string.Join(Environment.NewLine, entries));
        }

        if (!File.Exists(path))
        {
            return Error(request.ToolId, $"Skill resource was not found: {NormalizeDisplayPath(args.Path)}", "skill-resource-not-found");
        }

        if (await IsBinaryFileAsync(path, cancellationToken))
        {
            var resources = await ResolveResourcesAsync(
                profile,
                context.SessionId,
                context.Workspace,
                context.ExecutionBinding,
                selectedTarget,
                cancellationToken);
            var resource = resources.FirstOrDefault(item => string.Equals(item.ResourceId, skill.SkillId, StringComparison.OrdinalIgnoreCase));
            var content = new StringBuilder()
                .AppendLine("Binary skill resource.")
                .Append("Relative path: ").AppendLine(NormalizeDisplayPath(args.Path))
                .Append("Size: ").Append(new FileInfo(path).Length).AppendLine(" byte(s)");
            if (resource is not null)
            {
                content.Append("Executor path: ").AppendLine(CombineExecutionPath(resource.ExecutionPath, args.Path));
            }

            return Result(request.ToolId, "Skill resource is binary.", content.ToString().Trim());
        }

        var offset = args.Offset is > 0 ? args.Offset.Value : 1;
        var limit = args.Limit is > 0 ? Math.Min(args.Limit.Value, MaxReadLimit) : DefaultReadLimit;
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var selected = lines.Skip(offset - 1).Take(limit).Select((line, index) => $"{offset + index}: {line}").ToArray();
        var wasTruncated = offset - 1 + selected.Length < lines.Length;
        return Result(request.ToolId, $"Read skill resource '{NormalizeDisplayPath(args.Path)}'.",
            string.Join(Environment.NewLine, selected) + (wasTruncated ? Environment.NewLine + "[truncated]" : string.Empty), wasTruncated);
    }

    private string BuildSkillContent(InstalledSkillRecord skill, ParsedSkillMarkdown parsed, AgentResolvedExecutionResource? resource)
    {
        var builder = new StringBuilder();
        builder.Append("# Skill: ").AppendLine(SkillStore.ResolveDisplayName(skill));
        AppendMetadata(builder, "Skill id", skill.SkillId);
        AppendMetadata(builder, "Name", parsed.Name);
        AppendMetadata(builder, "Description", parsed.Description);
        AppendMetadata(builder, "Version", parsed.Version);
        AppendMetadata(builder, "Author", parsed.Author);
        if (parsed.Metadata.Count > 0)
        {
            builder.AppendLine().AppendLine("Metadata:");
            foreach (var pair in parsed.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("- ").Append(pair.Key).Append(": ").AppendLine(pair.Value);
            }
        }

        builder.AppendLine().AppendLine("Resource access:")
            .AppendLine("- Use `skill_resource` with relative paths to read bundled files.")
            .Append("- Host path: ").AppendLine(store.GetSkillRootPath(skill));
        if (resource is not null)
        {
            builder.Append("- Executor path: ").AppendLine(resource.ExecutionPath);
        }
        else
        {
            builder.AppendLine("- Executor path: not exposed by the selected execution target.");
        }

        builder.AppendLine().AppendLine("## Instructions").AppendLine(parsed.Body.Trim());
        return builder.ToString().Trim();
    }

    private async ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentProfileRecord profile,
        Guid? sessionId,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? executionBinding,
        ServiceReference<IExecutionTargetService>? selectedTarget,
        CancellationToken cancellationToken)
    {
        if (workspace is null || executionBinding is null || selectedTarget is null)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var descriptors = ListResources(profile);
        if (descriptors.Count == 0)
        {
            return [];
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selectedTarget.RetirementToken);
        try
        {
            await selectedTarget.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var service = selectedTarget.Service;
            var descriptor = await service.DescribeAsync(invocation.Token).ConfigureAwait(false);
            if ((!string.Equals(descriptor.TargetId, executionBinding.ContributionId, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(descriptor.TargetKind, executionBinding.ContributionId, StringComparison.OrdinalIgnoreCase))
                || !descriptor.Facets.Contains(AgentExecutionFacetIds.ResourceResolution, StringComparer.Ordinal)) return [];
            var result = await service.ResolveResourcesAsync(new()
            {
                Context = new()
                {
                    SessionId = sessionId, ProfileId = profile.ProfileId, Workspace = workspace, Binding = executionBinding,
                    AllowOutsideConfiguredScope = false, ApprovedResourceReferences = [], ApprovedResourceClaims = [],
                    ApprovedResourceCapabilities = [], ResourceOperation = null, CapturePostMutationResource = false, ExpectedConfigurationGeneration = null,
                },
                Resources = descriptors,
            }, invocation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await selectedTarget.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (Unavailable(error, selectedTarget.RetirementToken, cancellationToken)) { return []; }
    }

    private async ValueTask<AgentProfileRecord?> GetProfileAsync(string? profileId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var catalog = await catalogs.ListAsync(token).ConfigureAwait(false);
        var provider = catalog.Providers.SingleOrDefault(item => item.State == ServiceProviderState.Active);
        if (provider is null) return null;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
        try
        {
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var profile = await provider.Service.GetProfileAsync(profileId, invocation.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return profile;
        }
        catch (Exception error) when (Unavailable(error, provider.RetirementToken, token)) { return null; }
    }

    private static bool Unavailable(Exception error, CancellationToken retirement, CancellationToken caller)
        => !caller.IsCancellationRequested && (error is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }
            || retirement.IsCancellationRequested && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled });

    public async IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var channel = Channel.CreateBounded<AgentCapabilityCatalogChange>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        long revision = 0;
        void Changed() => channel.Writer.TryWrite(new() { Revision = Interlocked.Increment(ref revision) });
        store.SkillsChanged += Changed;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            { token.ThrowIfCancellationRequested(); yield return item; }
        }
        finally { store.SkillsChanged -= Changed; channel.Writer.TryComplete(); }
    }

    public ValueTask<AgentToolResult?> ValidateArgumentsAsync(AgentToolRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentPermissionRequest?>(null); }
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolPresentation?>(null); }
    public ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    private InstalledSkillRecord? ResolveEnabledSkill(AgentProfileRecord profile, string name)
    {
        var enabledIds = GetEnabledSkillIds(profile);
        return store.ListSkills().FirstOrDefault(skill => enabledIds.Contains(skill.SkillId)
                                                          && (string.Equals(skill.SkillId, name, StringComparison.OrdinalIgnoreCase)
                                                              || (!string.IsNullOrWhiteSpace(skill.Name)
                                                                  && string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))));
    }

    private IEnumerable<InstalledSkillRecord> ListEnabledSkills(AgentProfileRecord profile)
    {
        var enabledIds = GetEnabledSkillIds(profile);
        return store.ListSkills().Where(skill => enabledIds.Contains(skill.SkillId));
    }

    private static HashSet<string> GetEnabledSkillIds(AgentProfileRecord profile)
        => (profile.SelectableCapabilityAssignments ?? [])
            .Where(assignment => string.Equals(assignment.Kind, SkillConstants.CapabilityKind, StringComparison.OrdinalIgnoreCase)
                                 && (string.IsNullOrWhiteSpace(assignment.SourceId)
                                     || string.Equals(assignment.SourceId, SkillConstants.PackageId, StringComparison.OrdinalIgnoreCase)))
            .Select(assignment => assignment.CapabilityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasEnabledSkills(AgentProfileRecord? profile)
        => profile is not null && GetEnabledSkillIds(profile).Count > 0;

    private static string ResolveSkillResourcePath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Skill resource paths must be relative to the skill folder.");
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Skill resource paths cannot use traversal segments.");
        }

        var resolved = Path.GetFullPath(Path.Combine([root, .. segments]));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(normalizedRoot, StringComparison.Ordinal)
            && !string.Equals(resolved, Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Skill resource path is outside the skill folder.");
        }

        return resolved;
    }

    private static async Task<bool> IsBinaryFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, (int)Math.Min(new FileInfo(path).Length, 8192))];
        if (buffer.Length == 0)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.Take(read).Any(value => value == 0);
    }

    private static bool TryParseSkillArgs(string argumentsJson, out SkillArgs args, out string? error)
    {
        args = new SkillArgs(string.Empty);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("name", out var name, out error))
        {
            error = $"Invalid skill arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        args = new SkillArgs(name!);
        return true;
    }

    private static bool TryParseSkillResourceArgs(string argumentsJson, out SkillResourceArgs args, out string? error)
    {
        args = new SkillResourceArgs(string.Empty);
        if (!AgentToolArgumentObject.TryParse(argumentsJson, out var arguments, out error)
            || !arguments!.TryReadRequiredString("skill", out var skill, out error)
            || !arguments.TryReadOptionalString("path", out var path, out error)
            || !arguments.TryReadOptionalInt32("offset", out var offset, out error)
            || !arguments.TryReadOptionalInt32("limit", out var limit, out error))
        {
            error = $"Invalid skill_resource arguments: {error ?? "arguments were empty or invalid."}";
            return false;
        }

        args = new SkillResourceArgs(skill!, path, offset, limit);
        return true;
    }

    private static bool IsSkillTool(string toolId)
        => string.Equals(toolId, SkillConstants.SkillToolId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolId, SkillConstants.SkillResourceToolId, StringComparison.OrdinalIgnoreCase);

    private static string BuildStatusText(InstalledSkillRecord skill)
        => skill.SourceKind switch
        {
            "github" when !string.IsNullOrWhiteSpace(skill.ResolvedCommitSha) => $"Installed from GitHub at {skill.ResolvedCommitSha[..Math.Min(12, skill.ResolvedCommitSha.Length)]}.",
            "local" => "Installed from a local folder.",
            _ => "Installed skill.",
        };

    private static void AppendMetadata(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(label).Append(": ").AppendLine(value.Trim());
        }
    }

    private static string NormalizeDisplayPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/');

    private static string CombineExecutionPath(string root, string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? root
            : root.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars].TrimEnd() + Environment.NewLine + "[truncated]";

    private static AgentToolResult Error(string toolId, string message, string code)
        => Result(toolId, message, $"### Skill tool failed\n\n{message}") with { IsError = true, ErrorCode = code };

    private static AgentToolResult Result(string toolId, string summary, string content, bool truncated = false) => new()
    {
        ToolId = toolId, Summary = summary, Content = content, WasTruncated = truncated, BackendId = SkillConstants.PackageId,
        IsError = false, ErrorCode = null, StructuredPayloadJson = null, Sources = null, PresentationPayloadJson = null, RequiresPromptContextRefresh = false,
    };

    private sealed record SkillArgs(string Name);

    private sealed record SkillResourceArgs(string Skill, string? Path = null, int? Offset = null, int? Limit = null);

}
