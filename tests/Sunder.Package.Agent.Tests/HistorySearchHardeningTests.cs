using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class HistorySearchHardeningTests
{
    [Fact]
    public void SecretRedactor_CoversQuotedUnquotedColonEqualsAndEscapedAssignmentsPerLine()
    {
        var canaries = new[]
        {
            "quoted-double-canary",
            "quoted-single-canary",
            "unquoted-colon-canary",
            "unquoted-equals-canary",
            "json-canary",
            "escaped-canary",
            "authorization-canary",
        };
        var input = """
            password = "quoted-double-canary"
            client_secret:'quoted-single-canary'
            TOKEN: unquoted-colon-canary
            export SERVICE_API_KEY=unquoted-equals-canary
            "refresh_token": "json-canary", "safe": "preserved"
            \"api_key\":\"escaped-canary\"
            authorization: Basic authorization-canary
            keep-next-line
            """;

        var redacted = HistorySecretRedactor.Redact(input);

        Assert.All(canaries, canary => Assert.DoesNotContain(canary, redacted, StringComparison.Ordinal));
        Assert.Contains("keep-next-line", redacted, StringComparison.Ordinal);
        Assert.Contains("preserved", redacted, StringComparison.Ordinal);
        Assert.Equal(7, redacted.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SecretRedactor_FailsClosedAcrossQuotedAndYamlMultilineValues()
    {
        var input = """
            safe-before
            password: !vault &double "double-line-canary
            double-close-canary"
            safe-after-double
            client_secret: &single !vault 'single-line-canary''single-doubled-canary
            single-close-canary'
            safe-after-single
            api_key: !vault |-
              literal-strip-canary
            safe-after-literal-strip
            access_token: &literal !vault |+
              literal-keep-canary
            safe-after-literal-keep
            refresh_token: !vault >-
              folded-strip-canary
            safe-after-folded-strip
            token: &folded !vault >+
              folded-keep-canary
            safe-after-folded-keep
            secret: !vault |2-
              explicit-literal-canary
            safe-after-explicit-literal
            private_key: &explicit !vault >+2
              explicit-folded-canary
            safe-after-explicit-folded
            authorization: &authorization !vault
              Basic indented-line-canary
            safe-after-indented
            password: !vault 'inline-canary''inline-doubled-canary'; safe-inline-preserved
            """;

        var redacted = HistorySecretRedactor.Redact(input);

        Assert.DoesNotContain("canary", redacted, StringComparison.Ordinal);
        Assert.Contains("safe-before", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-double", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-single", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-literal-strip", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-literal-keep", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-folded-strip", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-folded-keep", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-explicit-literal", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-explicit-folded", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]\nsafe-after-indented", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]; safe-inline-preserved", redacted, StringComparison.Ordinal);

        var unterminated = HistorySecretRedactor.Redact("safe-prefix\npassword: \"unterminated-canary\nstill-secret-canary");
        Assert.Equal("safe-prefix\npassword: [REDACTED]", unterminated);

        var malformedTag = HistorySecretRedactor.Redact(
            "safe-prefix\npassword: !<unterminated-tag-canary\n  tagged-value-canary\nsafe-suffix");
        Assert.Equal("safe-prefix\npassword: [REDACTED]\nsafe-suffix", malformedTag);

        var taggedPlain = HistorySecretRedactor.Redact(
            "password: !vault &plain plain-head-canary\n  plain-continuation-canary\nsafe_property: retained");
        Assert.Equal("password: [REDACTED]\nsafe_property: retained", taggedPlain);

        var malformedWithBoundary = HistorySecretRedactor.Redact(
            "client_secret: &broken !vault \"broken-head-canary\n  broken-continuation-canary\nsafe_property: retained");
        Assert.Equal("client_secret: [REDACTED]\nsafe_property: retained", malformedWithBoundary);

        var malformedPlainWithBoundary = HistorySecretRedactor.Redact(
            "token: !vault &broken [broken-plain-head-canary\n  broken-plain-continuation-canary\nsafe_property: retained");
        Assert.Equal("token: [REDACTED]\nsafe_property: retained", malformedPlainWithBoundary);
    }

    [Fact]
    public void SecretRedactor_SanitizesOrdinaryHttpUrlsWithoutRemovingAdjacentSafeText()
    {
        var redacted = HistorySecretRedactor.Redact(
            "safe-before (HTTPS://url-user-canary:url-pass-canary@Example.COM:8443/safe/path?token=url-query-canary#url-fragment-canary), safe-after");

        Assert.Equal(
            "safe-before (https://example.com:8443/safe/path), safe-after",
            redacted);

        var ipv6 = HistorySecretRedactor.Redact(
            "safe-before http://[2001:db8::1]:8080/safe(path) safe-after");

        Assert.Equal(
            "safe-before http://[2001:db8::1]:8080/safe(path) safe-after",
            ipv6);

        var malformed = HistorySecretRedactor.Redact(
            "safe-before https://%?token=malformed-url-canary safe-after");

        Assert.Equal("safe-before [REDACTED URL] safe-after", malformed);

        var quotedPrivateValues = HistorySecretRedactor.Redact(
            "safe-before https://example.com/path?token=\"quoted-query-canary\", https://example.com/fragment#secret=`backtick-fragment-canary`; safe-after");

        Assert.Equal(
            "safe-before https://example.com/path, https://example.com/fragment; safe-after",
            quotedPrivateValues);

        var quotedUserInfo = HistorySecretRedactor.Redact(
            "safe-before https://user:\"quoted-userinfo-canary\"@example.com/path safe-after");

        Assert.DoesNotContain("quoted-userinfo-canary", quotedUserInfo, StringComparison.Ordinal);
        Assert.Contains("safe-before", quotedUserInfo, StringComparison.Ordinal);
        Assert.Contains("safe-after", quotedUserInfo, StringComparison.Ordinal);

        var fullyQuotedUserInfo = HistorySecretRedactor.Redact(
            "safe-before https://`quoted-user-canary`:\"quoted-password-canary\"@example.com/path safe-after");

        Assert.DoesNotContain("quoted-user-canary", fullyQuotedUserInfo, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted-password-canary", fullyQuotedUserInfo, StringComparison.Ordinal);
        Assert.Contains("safe-before", fullyQuotedUserInfo, StringComparison.Ordinal);
        Assert.Contains("safe-after", fullyQuotedUserInfo, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretRedactor_RedactsMatchedAndUnterminatedPemMaterial()
    {
        var matched = HistorySecretRedactor.Redact(
            "safe-before\n-----BEGIN CERTIFICATE-----\nmatched-pem-canary\n-----END CERTIFICATE-----\nsafe-after");
        Assert.Equal("safe-before\n[REDACTED PEM]\nsafe-after", matched);

        var bounded = HistorySecretRedactor.Redact(
            "safe-before\nprivate_key: |\n  -----BEGIN PRIVATE KEY-----\n  bounded-pem-canary\nsafe_property: retained");
        Assert.Equal("safe-before\nprivate_key: [REDACTED]\nsafe_property: retained", bounded);

        var throughEof = HistorySecretRedactor.Redact(
            "safe-before\n-----BEGIN PRIVATE KEY-----\neof-pem-canary");
        Assert.Equal("safe-before\n[REDACTED PEM]", throughEof);
    }

    [Fact]
    public async Task MultilineCredentials_NeverReachProjectionFtsSnippetOrSerializedHit()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Multiline redaction");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Safe session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            """
            safe-multiline-anchor
            password: !vault &double "projection-double-canary
            projection-double-tail-canary"
            safe-after-projection-double
            client_secret: &single !vault 'projection-single-canary''projection-single-doubled-canary
            projection-single-tail-canary'
            safe-after-projection-single
            api_key: !vault |-
              projection-literal-strip-canary
            safe-after-projection-literal-strip
            access_token: &literal !vault |+
              projection-literal-keep-canary
            safe-after-projection-literal-keep
            refresh_token: !vault >-
              projection-folded-strip-canary
            safe-after-projection-folded-strip
            secret: &folded !vault >+
              projection-folded-keep-canary
            safe-after-projection-folded-keep
            private_key: !<tag:yaml.org,2002:str> |2-
              projection-explicit-literal-canary
            safe-after-projection-explicit-literal
            password: &explicit !vault >+2
              projection-explicit-folded-canary
            safe-after-projection-explicit-folded
            token: &property-only !vault
              projection-indented-canary
            safe_after_projection_indented: retained-indented-boundary
            password: !vault &plain projection-plain-head-canary
              projection-plain-continuation-canary
              projection-plain-tail-canary
            safe_after_projection_plain: retained-plain-boundary
            client_secret: &broken !vault "projection-broken-head-canary
              projection-broken-continuation-canary
            safe_after_projection_broken: retained-broken-boundary
            token: !vault &broken-plain [projection-broken-plain-head-canary
              projection-broken-plain-continuation-canary
            safe_after_projection_broken_plain: retained-broken-plain-boundary
            certificate: |-
              -----BEGIN CERTIFICATE-----
              projection-matched-pem-canary
              -----END CERTIFICATE-----
            safe_after_projection_matched_pem: retained-matched-pem-boundary
            private_key: |-
              -----BEGIN PRIVATE KEY-----
              projection-unterminated-pem-canary
            safe_tail: safe-multiline-tail
            """);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var runtimeState = services.GetRequiredService<HistorySearchRuntimeState>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitForSearchReadinessAsync(
            projection,
            runtimeState,
            search,
            "safe-multiline-tail",
            expectedDocumentCount: 1);
        runtimeState.Publish(status => status with
        {
            FailureMessage = "safe-state-prefix\n-----BEGIN PRIVATE KEY-----\nstate-unterminated-pem-canary",
        });

        var response = await search.SearchAsync(new HistorySearchRequest("safe-multiline-tail"));
        var hit = Assert.Single(response.Results);
        var state = await search.GetStateAsync(new HistorySearchStateRequest(
            IncludeAdvancedFilters: true));
        using var connection = Open(projection.DatabasePath);
        var persisted = ExecuteString(
            connection,
            "SELECT BodyText || char(10) || DisplaySnippet FROM HistoryDocuments LIMIT 1;");
        var fts = ExecuteString(
            connection,
            "SELECT BodyText FROM HistoryDocumentsFts LIMIT 1;");
        var serialized = JsonSerializer.Serialize(new { Hit = hit, State = state });

        foreach (var canary in new[]
                 {
                     "projection-double-canary",
                     "projection-double-tail-canary",
                     "projection-single-canary",
                     "projection-single-doubled-canary",
                     "projection-single-tail-canary",
                     "projection-literal-strip-canary",
                     "projection-literal-keep-canary",
                     "projection-folded-strip-canary",
                     "projection-folded-keep-canary",
                     "projection-explicit-literal-canary",
                     "projection-explicit-folded-canary",
                     "projection-indented-canary",
                     "projection-plain-head-canary",
                     "projection-plain-continuation-canary",
                     "projection-plain-tail-canary",
                     "projection-broken-head-canary",
                     "projection-broken-continuation-canary",
                     "projection-broken-plain-head-canary",
                     "projection-broken-plain-continuation-canary",
                     "projection-matched-pem-canary",
                     "projection-unterminated-pem-canary",
                     "state-unterminated-pem-canary",
                 })
        {
            Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, fts, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, hit.Snippet, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);
        }
        Assert.Contains("retained-plain-boundary", persisted, StringComparison.Ordinal);
        Assert.Contains("retained-broken-boundary", persisted, StringComparison.Ordinal);
        Assert.Contains("retained-broken-plain-boundary", persisted, StringComparison.Ordinal);
        Assert.Contains("retained-matched-pem-boundary", persisted, StringComparison.Ordinal);
        Assert.Contains("safe-multiline-tail", hit.Snippet, StringComparison.Ordinal);
        Assert.Contains("safe-state-prefix", state.Status.FailureMessage, StringComparison.Ordinal);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task OrdinaryHttpUrls_AreSanitizedAcrossProjectionFtsAndSerializedOutput()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var profiles = services.GetRequiredService<AgentProfileService>();
        var sessions = services.GetRequiredService<AgentSessionService>();
        var workspace = workspaces.CreateWorkspace(
            "Workspace https://workspace-user-canary:workspace-pass-canary@workspace.example/safe/workspace?private=\"workspace-query-canary\"#workspace-fragment-canary");
        var profile = await profiles.CreateProfileAsync(
            "Profile http://profile-user-canary:profile-pass-canary@profile.example:8080/safe/profile?token='profile-query-canary'#profile-fragment-canary");
        var session = sessions.CreateSession(
            "Session https://session-user-canary:session-pass-canary@session.example/safe/session?secret=`session-query-canary`#private=\"session-fragment-canary\"",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            "Visit (https://body-user-canary:body-pass-canary@body.example:8443/safe/body?api_key=\"body-query-canary\"#private='body-fragment-canary'), then keep url-safe-tail-anchor.");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var runtimeState = services.GetRequiredService<HistorySearchRuntimeState>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitForSearchReadinessAsync(
            projection,
            runtimeState,
            search,
            "url-safe-tail-anchor",
            expectedDocumentCount: 1);
        runtimeState.Publish(status => status with
        {
            FailureMessage = "Status https://status-user-canary:status-pass-canary@status.example/safe/status?token=`status-query-canary`#private=\"status-fragment-canary\"",
        });

        var response = await search.SearchAsync(new HistorySearchRequest("url-safe-tail-anchor"));
        var hit = Assert.Single(response.Results);
        var state = await search.GetStateAsync(new HistorySearchStateRequest(
            IncludeAdvancedFilters: true));
        using var connection = Open(projection.DatabasePath);
        var persisted = ExecuteString(
            connection,
            "SELECT WorkspaceName || char(10) || SessionTitle || char(10) || BodyText || char(10) || DisplaySnippet FROM HistoryDocuments LIMIT 1;");
        var fts = ExecuteString(
            connection,
            "SELECT BodyText || char(10) || PathText || char(10) || SymbolText || char(10) || ActivityText || char(10) || ToolText FROM HistoryDocumentsFts LIMIT 1;");
        var serialized = JsonSerializer.Serialize(new { Hit = hit, State = state });
        var canaries = new[]
        {
            "workspace-user-canary", "workspace-pass-canary", "workspace-query-canary", "workspace-fragment-canary",
            "profile-user-canary", "profile-pass-canary", "profile-query-canary", "profile-fragment-canary",
            "session-user-canary", "session-pass-canary", "session-query-canary", "session-fragment-canary",
            "body-user-canary", "body-pass-canary", "body-query-canary", "body-fragment-canary",
            "status-user-canary", "status-pass-canary", "status-query-canary", "status-fragment-canary",
        };

        Assert.All(canaries, canary =>
        {
            Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, fts, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);
        });
        Assert.Contains("https://body.example:8443/safe/body", hit.Snippet, StringComparison.Ordinal);
        Assert.Contains("https://workspace.example/safe/workspace", hit.WorkspaceName, StringComparison.Ordinal);
        Assert.Contains("https://session.example/safe/session", hit.SessionTitle, StringComparison.Ordinal);
        Assert.Contains(state.Profiles, option => option.DisplayName.Contains(
            "http://profile.example:8080/safe/profile",
            StringComparison.Ordinal));
        Assert.Contains("https://status.example/safe/status", state.Status.FailureMessage, StringComparison.Ordinal);
        await indexer.StopAsync();
    }

    [Fact]
    public void Extractor_UsesExactToolSchemasAndNeverIndexesCommandsOrSearchPatterns()
    {
        var session = CreateSourceSession();
        var lookalike = Assert.Single(HistorySearchExtractor.Extract(
            session,
            CreateToolTurn(
                "vendor_apply_patch_backup",
                "{\"patchText\":\"*** Update File: secret.txt\",\"path\":\"secret.txt\",\"operation\":\"delete\"}",
                AgentToolExecutionStatus.Ambiguous)));

        Assert.Equal(HistoryActivityKind.Use, lookalike.Activity);
        Assert.DoesNotContain(lookalike.Facets, facet => facet.Kind is "path" or "operation" or "metadata");
        Assert.DoesNotContain("secret.txt", lookalike.BodyText, StringComparison.Ordinal);
        Assert.Contains(lookalike.Facets, facet => facet.Kind == "status" && facet.Value == "Ambiguous");

        var shell = Assert.Single(HistorySearchExtractor.Extract(
            session,
            CreateToolTurn(
                "shell",
                "{\"command\":\"export API_KEY=raw-secret\",\"workingDirectory\":\"src\"}",
                ownerPackageId: "sunder.package.agent.tools.shell")));
        Assert.Equal(HistoryActivityKind.Execute, shell.Activity);
        Assert.DoesNotContain(shell.Facets, facet => facet.Kind == "command");
        Assert.DoesNotContain("raw-secret", shell.BodyText, StringComparison.Ordinal);
        Assert.Contains(shell.Facets, facet => facet.Kind == "working-directory" && facet.Value == "src");

        var grep = Assert.Single(HistorySearchExtractor.Extract(
            session,
            CreateToolTurn(
                "grep",
                "{\"pattern\":\"password=super-secret\",\"path\":\"src\",\"query\":\"bypass\"}",
                ownerPackageId: "sunder.package.agent.tools.files")));
        Assert.Equal(HistoryActivityKind.Search, grep.Activity);
        Assert.DoesNotContain(grep.Facets, facet => facet.Kind == "pattern");
        Assert.DoesNotContain("super-secret", grep.BodyText, StringComparison.Ordinal);
        Assert.Contains(grep.Facets, facet => facet.Kind == "path" && facet.Value == "src");
    }

    [Fact]
    public void Extractor_StripsUrlAuthoritySecretsQueryAndFragment()
    {
        var activity = Assert.Single(HistorySearchExtractor.Extract(
            CreateSourceSession(),
            CreateToolTurn(
                "web_fetch",
                "{\"url\":\"https://alice:password@example.com:8443/path/file?api_key=top-secret#token\"}",
                ownerPackageId: "sunder.package.agent.tools.web")));

        var url = Assert.Single(activity.Facets, facet => facet.Kind == "url");
        Assert.Equal("https://example.com:8443/path/file", url.Value);
        Assert.DoesNotContain("alice", activity.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("password", activity.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", activity.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("token", activity.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("apply_patch", "{\"patchText\":\"*** Update File: private-patch.txt\\n-old\\n+new\"}", "private-patch.txt")]
    [InlineData("read", "{\"path\":\"private-read.txt\"}", "private-read.txt")]
    [InlineData("shell", "{\"command\":\"do-not-index\",\"workingDirectory\":\"private-shell\"}", "private-shell")]
    [InlineData("grep", "{\"pattern\":\"do-not-index\",\"path\":\"private-grep\"}", "private-grep")]
    [InlineData("web_fetch", "{\"url\":\"https://private.example/opaque\"}", "private.example")]
    public void Extractor_TreatsUnresolvedAndCollidingAllowlistedToolsAsOpaque(
        string toolId,
        string argumentsJson,
        string privateValue)
    {
        foreach (var ownerPackageId in new string?[] { null, "vendor.colliding.tools" })
        {
            var document = Assert.Single(HistorySearchExtractor.Extract(
                CreateSourceSession(),
                CreateToolTurn(
                    toolId,
                    argumentsJson,
                    AgentToolExecutionStatus.Completed,
                    ownerPackageId)));

            Assert.Equal(HistoryActivityKind.Use, document.Activity);
            Assert.Equal(
                ["status", "tool"],
                document.Facets.Select(static facet => facet.Kind).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(privateValue, document.BodyText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ReadyToolCatalog_FailsClosedWhenDifferentPackagesClaimTheSameToolId()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddExtension(
            PackageExtensionPoints.Tools,
            new CatalogTool("shell"),
            "sunder.package.agent.tools.shell");
        await using var services = CreateRuntime(scope, catalog);
        var toolService = services.GetRequiredService<AgentToolService>();
        var advertised = Assert.Single(
            await toolService.ListReadyOwnedRuntimeToolsAsync(),
            tool => tool.RuntimeTool.Descriptor.ToolId == "shell");
        catalog.AddExtension(
            PackageExtensionPoints.Tools,
            new CatalogTool("SHELL"),
            "vendor.colliding.tools");

        var ready = await toolService.ListReadyOwnedRuntimeToolsAsync();
        var denied = await toolService.ExecuteAsync(
            "shell",
            "{}",
            advertisedDescriptor: advertised.RuntimeTool.Descriptor,
            advertisedOwnerPackageId: advertised.OwnerPackageId);

        Assert.DoesNotContain(
            ready,
            tool => string.Equals(
                tool.RuntimeTool.Descriptor.ToolId,
                "shell",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(denied.IsError);
        Assert.Equal(AgentToolSecurityErrorCodes.NotAdvertised, denied.ErrorCode);
    }

    [Fact]
    public async Task VendorShell_RemainsOpaqueAfterRemovalAndFirstPartyReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var vendorShell = new CatalogTool("shell");
        catalog.AddExtension(PackageExtensionPoints.Tools, vendorShell, "vendor.shell.tools");
        await using var services = CreateRuntime(scope, catalog);
        var toolService = services.GetRequiredService<AgentToolService>();
        var ownerAtPreparation = Assert.Single(
            await toolService.ListReadyOwnedRuntimeToolsAsync(),
            tool => tool.RuntimeTool.Descriptor.ToolId == "shell");
        var workspace = services.GetRequiredService<AgentWorkspaceService>()
            .CreateWorkspace("Vendor provenance");
        var session = services.GetRequiredService<AgentSessionService>()
            .CreateSession("Vendor shell", workspaceId: workspace.WorkspaceId);
        var store = services.GetRequiredService<AgentLocalStore>();
        PrepareHistoryToolExecution(
            store,
            session,
            "shell",
            "{\"command\":\"HistoryVendorCommandSecret\",\"workingDirectory\":\"VendorOnlyDirectory\"}",
            isReadOnly: false,
            ownerPackageId: ownerAtPreparation.OwnerPackageId);

        catalog.RemoveExtension(PackageExtensionPoints.Tools, vendorShell);
        catalog.AddExtension(
            PackageExtensionPoints.Tools,
            new CatalogTool("shell"),
            "sunder.package.agent.tools.shell");
        var replacement = Assert.Single(
            await toolService.ListReadyOwnedRuntimeToolsAsync(),
            tool => tool.RuntimeTool.Descriptor.ToolId == "shell");
        Assert.Equal("sunder.package.agent.tools.shell", replacement.OwnerPackageId);

        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);

        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("VendorOnlyDirectory"))).Results);
        Assert.Empty((await search.SearchAsync(new HistorySearchRequest("HistoryVendorCommandSecret"))).Results);
        var shellActivity = Assert.Single(
            (await search.SearchAsync(new HistorySearchRequest("shell"))).Results);
        Assert.Equal(HistoryActivityKind.Use, shellActivity.Activity);
        Assert.Empty(shellActivity.Paths);
        var persistedItem = Assert.Single(store.ListTurns(session.SessionId)).Items[0];
        Assert.Equal("vendor.shell.tools", persistedItem.ToolOwnerPackageId);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task FirstPartyPersistedOwner_KeepsPathActivitySearchable()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>()
            .CreateWorkspace("First-party provenance");
        var session = services.GetRequiredService<AgentSessionService>()
            .CreateSession("First-party read", workspaceId: workspace.WorkspaceId);
        PrepareHistoryToolExecution(
            services.GetRequiredService<AgentLocalStore>(),
            session,
            "read",
            "{\"path\":\"src/OwnedHistory.cs\"}",
            isReadOnly: true,
            ownerPackageId: "sunder.package.agent.tools.files");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);

        var activity = Assert.Single(
            (await search.SearchAsync(new HistorySearchRequest("OwnedHistory"))).Results);
        Assert.Equal(HistoryActivityKind.Read, activity.Activity);
        Assert.Contains("src/OwnedHistory.cs", activity.Paths);
        await indexer.StopAsync();
    }

    [Fact]
    public void FacetSearch_AllowsOnlyExactAndPrefixMatches()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var document = CreateDocument("unrelated body", "src/prefix-target.cs");
        projection.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);
        projection.ActivateTextGeneration(generation);

        Assert.Single(projection.SearchLexical(generation, new HistorySearchRequest("prefix"), 10));
        Assert.Empty(projection.SearchLexical(generation, new HistorySearchRequest("refix"), 10));
    }

    [Fact]
    public async Task EmbeddingCatalog_RequiresAuthoritativeOwnerAndFingerprintsImplementations()
    {
        var catalog = new RegressionTestExtensionCatalog();
        var first = new FirstFingerprintProvider("DUPLICATE", "MODEL");
        var second = new SecondFingerprintProvider("duplicate", "model");
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, first, "package.one");
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, second, "package.two");
        var providers = new HistoryEmbeddingProviderCatalog(catalog);

        Assert.Throws<InvalidOperationException>(() => providers.Resolve(null, "duplicate"));
        var owned = providers.Resolve("PACKAGE.ONE", "duplicate");
        Assert.True(owned.Reference.TryAcquire(out var providerLease));
        using (providerLease)
        {
            Assert.Same(first, providerLease.Contribution);
        }
        Assert.Equal(2, providers.ListOptions().Count);

        var firstSelection = await providers.ResolveSelectionAsync(
            "PACKAGE.ONE", "duplicate", "MODEL", CancellationToken.None);
        var secondSelection = await providers.ResolveSelectionAsync(
            "package.two", "DUPLICATE", "model", CancellationToken.None);
        Assert.Equal("package.one", firstSelection.OwnedProvider.PackageId);
        Assert.Equal("DUPLICATE", firstSelection.OwnedProvider.ProviderId);
        Assert.Equal("MODEL", firstSelection.ModelId);
        Assert.NotEqual(firstSelection.SpaceFingerprint, secondSelection.SpaceFingerprint);
    }

    [Fact]
    public async Task EmbeddingCatalog_RedactsDisplayTextButPreservesOpaqueIdentifiersExactly()
    {
        const string packageId = "package.one";
        const string providerId = "provider-token=opaque-e\u0301";
        const string modelId = "model-api_key=opaque-e\u0301";
        var catalog = new RegressionTestExtensionCatalog();
        var provider = new DisplayNameFingerprintProvider(
            providerId,
            modelId,
            "Useful provider password=display-canary");
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, provider, packageId);
        var providers = new HistoryEmbeddingProviderCatalog(catalog);

        var option = Assert.Single(providers.ListOptions());
        var resolved = providers.Resolve(packageId, providerId);
        var selection = await providers.ResolveSelectionAsync(
            packageId,
            providerId,
            modelId,
            CancellationToken.None);

        Assert.Equal(packageId, option.PackageId);
        Assert.Equal(providerId, option.ProviderId);
        Assert.Equal(packageId, resolved.PackageId);
        Assert.Equal(providerId, resolved.ProviderId);
        Assert.Equal(modelId, selection.ModelId);
        Assert.Contains("Useful provider", option.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("display-canary", option.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAndState_PreserveCredentialShapedDecomposedOpaqueIdentifiersExactly()
    {
        const string workspaceId = "workspace-password=opaque-e\u0301";
        const string profileId = "profile-token=opaque-e\u0301";
        const string callId = "call-client_secret=opaque-e\u0301";
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var store = services.GetRequiredService<AgentLocalStore>();
        var now = DateTimeOffset.UtcNow;
        store.SaveWorkspace(new AgentWorkspaceRecord(
            workspaceId,
            "Exact workspace",
            null,
            now,
            now));
        store.SaveProfile(new AgentProfileRecord(
            profileId,
            "Exact profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now));
        var session = services.GetRequiredService<AgentSessionService>().CreateSession(
            "Exact session",
            profileId: profileId,
            workspaceId: workspaceId);
        PrepareHistoryToolExecution(
            store,
            session,
            "read",
            "{\"path\":\"src/OpaqueHistoryPath.cs\"}",
            isReadOnly: true,
            ownerPackageId: "sunder.package.agent.tools.files",
            callId: callId);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);

        var hit = Assert.Single((await search.SearchAsync(new HistorySearchRequest(
            "OpaqueHistoryPath",
            WorkspaceId: workspaceId,
            ProfileId: profileId))).Results);
        var state = await search.GetStateAsync(new HistorySearchStateRequest(
            IncludeAdvancedFilters: true));

        Assert.Equal(workspaceId, hit.WorkspaceId);
        Assert.Equal(profileId, hit.ProfileId);
        Assert.Equal(callId, hit.CallId);
        Assert.Contains(state.Workspaces, option => option.Id == workspaceId);
        Assert.Contains(state.Profiles, option => option.Id == profileId);
        Assert.Contains(state.Sessions, option => option.Id == session.SessionId.ToString("D")
                                                  && option.ParentId == workspaceId);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task EmbeddingCatalog_FingerprintIncludesProviderConfigurationIdentity()
    {
        var catalog = new RegressionTestExtensionCatalog();
        var provider = new ConfigurableFingerprintProvider("configured", "Model-V1")
        {
            SpaceIdentity = "https://first.example/v1",
        };
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, provider, "package.configured");
        var providers = new HistoryEmbeddingProviderCatalog(catalog);

        var first = await providers.ResolveSelectionAsync(
            "package.configured", "CONFIGURED", "Model-V1", CancellationToken.None);
        provider.SpaceIdentity = "https://second.example/v1";
        var second = await providers.ResolveSelectionAsync(
            "package.configured", "configured", "Model-V1", CancellationToken.None);

        Assert.Equal("Model-V1", second.ModelId);
        Assert.NotEqual(first.SpaceFingerprint, second.SpaceFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => providers.ResolveSelectionAsync(
            "package.configured", "configured", "model-v1", CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeStartup_ForcesSemanticOffRemovesEmbeddingsAndNeverCallsProvider()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var embeddings = new ReadinessEmbeddingProvider();
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, embeddings, "package.ready");
        var seededProjection = CreateReadyEmbeddingProjection(scope.Context);
        Assert.True(seededProjection.GetConfiguration().SemanticEnabled);
        Assert.True(seededProjection.GetSnapshot().SemanticReady);
        await using var services = CreateRuntime(scope, catalog);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => services.GetRequiredService<HistorySearchRuntimeState>()
            .Current.Availability == HistorySearchAvailability.Ready);
        var state = await search.GetStateAsync(new HistorySearchStateRequest());
        var response = await search.SearchAsync(new HistorySearchRequest("conceptually related"));

        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.Null(projection.GetSnapshot().ActiveEmbeddingGenerationId);
        Assert.Equal(0, projection.GetSnapshot().EmbeddingCount);
        Assert.False(projection.GetSnapshot().SemanticReady);
        Assert.False(state.Status.SemanticEnabled);
        Assert.Empty(state.EmbeddingProviders);
        Assert.Empty(state.EmbeddingModels);
        Assert.Empty(response.Results);
        Assert.Equal(0, embeddings.CallCount);
        await indexer.StopAsync();
    }

    [Fact]
    public void ConfigurationRevision_FencesStaleSavesAndManualClearPersistsAcrossRestart()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var textGeneration = projection.BeginTextGeneration();
        var document = CreateDocument("semantic body");
        projection.ReplaceTurnDocuments(textGeneration, document.SessionId, document.TurnId, [document]);
        projection.ActivateTextGeneration(textGeneration);
        var first = projection.ChangeConfiguration(true, "package.one", "provider", "model", new string('a', 64));
        var embeddingGeneration = projection.BeginEmbeddingGeneration(textGeneration, first);
        var work = Assert.Single(projection.ListEmbeddingWorkPage(textGeneration, null, 10));
        Assert.False(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("MODEL", [1f, 0f]),
            "model",
            null,
            out _,
            out _));
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [1f, 0f]),
            "model",
            null,
            out var dimensions,
            out var vector));

        var second = projection.ChangeConfiguration(true, "package.two", "provider", "model", new string('b', 64));

        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.Throws<OperationCanceledException>(() => projection.SaveEmbedding(
            embeddingGeneration,
            textGeneration,
            first,
            work,
            dimensions,
            vector));
        Assert.Equal(0, projection.GetSnapshot().EmbeddingCount);

        var cleared = projection.ClearDerivedIndex();
        var restarted = new HistorySearchStore(scope.Context);
        Assert.True(restarted.GetSnapshot().IsManuallyCleared);
        Assert.Equal(cleared.Revision, restarted.GetConfiguration().Revision);
        Assert.Null(restarted.GetSnapshot().ActiveTextGenerationId);
    }

    [Fact]
    public async Task LegacyManualClear_IsClearedAndAuthoritativeHistoryRebuildsOnStartup()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Manual clear recovery");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Recovered session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "recover legacy manual clear");
        var projection = services.GetRequiredService<HistorySearchStore>();
        projection.ClearDerivedIndex();
        Assert.True(projection.GetSnapshot().IsManuallyCleared);

        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);

        Assert.False(projection.GetSnapshot().IsManuallyCleared);
        Assert.NotNull(projection.GetSnapshot().ActiveTextGenerationId);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task CurrentSessionStartup_CancelsAndDrainsDormantSemanticWorkBeforeDisabling()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var fence = services.GetRequiredService<HistorySemanticOperationFence>();
        using var lease = fence.Begin(projection.GetConfiguration().Revision, CancellationToken.None);
        var configuration = projection.ChangeConfiguration(
            true,
            "package.dormant",
            "dormant-provider",
            "dormant-model",
            new string('f', 64));
        Assert.True(configuration.SemanticEnabled);

        var startup = indexer.StartAsync();
        await WaitUntilAsync(() => lease.CancellationToken.IsCancellationRequested);
        Assert.False(startup.IsCompleted);
        lease.Dispose();
        await startup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.Null(projection.GetSnapshot().ActiveEmbeddingGenerationId);
        Assert.Equal(0, projection.GetSnapshot().EmbeddingCount);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task SemanticCommand_IsForcedOffWithoutProviderCatalogOrQueryCalls()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var embeddings = new ReadinessEmbeddingProvider();
        catalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, embeddings, "package.readiness");
        await using var services = CreateRuntime(scope, catalog);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await search.ExecuteCommandAsync(new HistorySearchCommand(
            HistorySearchCommandKind.ConfigureSemantic,
            SemanticEnabled: true,
            EmbeddingProviderId: ReadinessEmbeddingProvider.ProviderId,
            EmbeddingModelId: ReadinessEmbeddingProvider.ModelId,
            EmbeddingProviderPackageId: "package.readiness"));
        await search.GetStateAsync(new HistorySearchStateRequest(
            EmbeddingProviderId: ReadinessEmbeddingProvider.ProviderId,
            EmbeddingProviderPackageId: "package.readiness"));
        await search.SearchAsync(new HistorySearchRequest("query that must remain local"));

        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.False(projection.GetSnapshot().SemanticReady);
        Assert.Equal(0, embeddings.CallCount);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task StartupReconciliation_PhysicallyPurgesADeletionMissedWhileStopped()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        Guid sessionId;
        await using (var firstRuntime = CreateRuntime(scope, catalog))
        {
            var workspace = firstRuntime.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Deletion");
            var sessions = firstRuntime.GetRequiredService<AgentSessionService>();
            var session = sessions.CreateSession("Delete me", workspaceId: workspace.WorkspaceId);
            sessionId = session.SessionId;
            sessions.AppendTextTurn(sessionId, AgentMessageRole.User, "physically purge me");
            var indexer = firstRuntime.GetRequiredService<HistorySearchIndexingService>();
            var projection = firstRuntime.GetRequiredService<HistorySearchStore>();
            await indexer.StartAsync();
            await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
            await indexer.StopAsync();
            firstRuntime.GetRequiredService<AgentLocalStore>().DeleteSessionTree(sessionId);
        }

        await using var secondRuntime = CreateRuntime(scope, catalog);
        var secondIndexer = secondRuntime.GetRequiredService<HistorySearchIndexingService>();
        var secondProjection = secondRuntime.GetRequiredService<HistorySearchStore>();
        var secondState = secondRuntime.GetRequiredService<HistorySearchRuntimeState>();
        await secondIndexer.StartAsync();
        await WaitUntilAsync(() => secondState.Current.Availability == HistorySearchAvailability.Ready
                                   && secondProjection.GetSnapshot().DocumentCount == 0);
        using var connection = Open(secondProjection.DatabasePath);
        Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryDocuments;"));
        Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryEmbeddings;"));
        await secondIndexer.StopAsync();
    }

    [Fact]
    public async Task ExtractorUpgrade_FailsClosedUntilCurrentProjectionRebuildSucceeds()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var sessions = services.GetRequiredService<AgentSessionService>();
        var store = services.GetRequiredService<AgentLocalStore>();
        var workspace = workspaces.CreateWorkspace("Extractor upgrade");
        var session = sessions.CreateSession("Legacy activity", workspaceId: workspace.WorkspaceId);
        PrepareHistoryToolExecution(
            store,
            session,
            "vendor_legacy_search",
            "{\"path\":\"LegacyUnsafeActivityPath\",\"pattern\":\"LegacyUnsafePattern\"}",
            isReadOnly: true,
            ownerPackageId: "vendor.legacy.tools");
        var legacyTurn = Assert.Single(store.ListTurns(session.SessionId));
        var legacyItem = Assert.Single(legacyTurn.Items);
        sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            "current provenance-safe search is restored");

        var projection = services.GetRequiredService<HistorySearchStore>();
        var legacyGeneration = projection.BeginTextGeneration();
        var legacyDocument = new HistoryProjectionDocument(
            HistorySearchExtractor.CreateDocumentId(legacyItem.ItemId, 0, HistoryAnchorKind.Activity),
            workspace.WorkspaceId,
            workspace.DisplayName,
            session.SessionId,
            session.Title,
            session.RootSessionId,
            session.ParentSessionId,
            session.ProfileId,
            Role: null,
            HistoryActivityKind.Search,
            legacyTurn.TurnId,
            legacyItem.ItemId,
            legacyItem.CallId,
            HistoryAnchorKind.Activity,
            legacyTurn.ContentRevision,
            legacyTurn.IsStreaming,
            legacyTurn.CreatedAtUtc,
            "vendor_legacy_search LegacyUnsafeActivityPath LegacyUnsafePattern",
            "Legacy unsafe activity: LegacyUnsafeActivityPath",
            [new HistoryProjectionFacet("path", "LegacyUnsafeActivityPath", "legacyunsafeactivitypath")]);
        projection.ReplaceTurnDocuments(
            legacyGeneration,
            session.SessionId,
            legacyTurn.TurnId,
            [legacyDocument]);
        projection.ActivateTextGeneration(legacyGeneration);
        var semanticConfiguration = projection.ChangeConfiguration(
            true,
            "legacy.embedding.package",
            "legacy-provider",
            "legacy-model",
            new string('e', 64));
        var legacyEmbeddingGeneration = projection.BeginEmbeddingGeneration(
            legacyGeneration,
            semanticConfiguration);
        var embeddingWork = Assert.Single(projection.ListEmbeddingWorkPage(
            legacyGeneration,
            afterDocumentId: null,
            limit: 10));
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("legacy-model", [1f, 0f]),
            "legacy-model",
            expectedDimensions: null,
            out var dimensions,
            out var vector));
        projection.SaveEmbedding(
            legacyEmbeddingGeneration,
            legacyGeneration,
            semanticConfiguration,
            embeddingWork,
            dimensions,
            vector);
        projection.ActivateEmbeddingGeneration(
            legacyEmbeddingGeneration,
            dimensions,
            semanticConfiguration);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE HistoryProjectionGenerations
                SET ExtractorVersion = $extractorVersion,
                    RedactionVersion = $redactionVersion;
                """;
            command.Parameters.AddWithValue("$extractorVersion", HistorySearchVersions.Extractor - 1);
            command.Parameters.AddWithValue("$redactionVersion", HistorySearchVersions.Redaction - 1);
            command.ExecuteNonQuery();
        }
        Assert.Null(projection.TryPinActiveProjection());

        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE AgentSessions RENAME TO AgentSessionsUpgradeFailure;";
            command.ExecuteNonQuery();
        }
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var search = services.GetRequiredService<HistorySearchService>();
        var runtimeState = services.GetRequiredService<HistorySearchRuntimeState>();
        await indexer.StartAsync();

        var rebuilding = await search.SearchAsync(new HistorySearchRequest("LegacyUnsafeActivityPath"));
        Assert.Empty(rebuilding.Results);
        Assert.Null(projection.TryPinActiveProjection());
        await WaitUntilAsync(() => runtimeState.Current.FailureCode == "text-rebuild-failed");
        var failed = await search.SearchAsync(new HistorySearchRequest("LegacyUnsafeActivityPath"));
        Assert.Empty(failed.Results);
        Assert.Equal(HistorySearchAvailability.Unavailable, failed.Status.Availability);
        Assert.Null(projection.TryPinActiveProjection());
        Assert.False(projection.GetConfiguration().SemanticEnabled);
        Assert.True(projection.GetConfiguration().Revision > semanticConfiguration.Revision);
        Assert.False(projection.GetSnapshot().IsManuallyCleared);
        using (var connection = Open(projection.DatabasePath))
        {
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryDocuments;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryDocumentsFts;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryEmbeddings;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM HistoryProjectionGenerations;"));
        }

        using (var connection = Open(store.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE AgentSessionsUpgradeFailure RENAME TO AgentSessions;";
            command.ExecuteNonQuery();
        }
        await WaitUntilAsync(() => projection.TryPinActiveProjection() is not null
                                   && projection.GetSnapshot().DocumentCount == 2);

        var currentGeneration = Assert.IsType<HistoryProjectionGeneration>(
            projection.GetActiveTextGeneration());
        Assert.Equal(HistorySearchVersions.Extractor, currentGeneration.ExtractorVersion);
        Assert.Equal(HistorySearchVersions.Redaction, currentGeneration.RedactionVersion);
        var restored = await search.SearchAsync(
            new HistorySearchRequest("current provenance-safe search is restored"));
        Assert.Contains(restored.Results, static result =>
            result.Snippet.Contains(
                "current provenance-safe search is restored",
                StringComparison.Ordinal));
        Assert.Empty((await search.SearchAsync(
            new HistorySearchRequest("LegacyUnsafeActivityPath"))).Results);
        Assert.False(projection.GetConfiguration().SemanticEnabled);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task RedactionUpgrade_RecreatesDisposableDatabaseAndErasesDatabaseWalAndShmBytes()
    {
        const string canary = "legacy-redaction-plaintext-canary-4f73b2a9";
        using var scope = RegressionTestPackageScope.Create();
        string databasePath;
        await using (var legacyServices = CreateRuntime(scope, new RegressionTestExtensionCatalog()))
        {
            var legacyProjection = legacyServices.GetRequiredService<HistorySearchStore>();
            var document = CreateDocument(canary);
            var legacyGeneration = legacyProjection.BeginTextGeneration();
            legacyProjection.ReplaceTurnDocuments(
                legacyGeneration,
                document.SessionId,
                document.TurnId,
                [document]);
            legacyProjection.ActivateTextGeneration(legacyGeneration);
            databasePath = legacyProjection.DatabasePath;
            using var connection = Open(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE HistoryProjectionGenerations
                SET RedactionVersion = $redactionVersion
                WHERE GenerationId = $generationId;
                UPDATE HistorySchemaMarker
                SET Version = $schemaVersion
                WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$redactionVersion", HistorySearchVersions.Redaction - 1);
            command.Parameters.AddWithValue("$generationId", legacyGeneration);
            command.Parameters.AddWithValue("$schemaVersion", HistorySearchVersions.Schema - 1);
            command.ExecuteNonQuery();
        }
        Assert.True(DatabaseFilesContain(databasePath, canary));

        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var projection = services.GetRequiredService<HistorySearchStore>();
        Assert.Null(projection.GetSnapshot().ActiveTextGenerationId);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().ActiveTextGenerationId is not null
                                   && projection.GetSnapshot().DocumentCount == 0);

        AssertDatabaseFilesDoNotContain(databasePath, canary);
        Assert.Equal(
            HistorySearchVersions.Redaction,
            Assert.IsType<HistoryProjectionGeneration>(projection.GetActiveTextGeneration()).RedactionVersion);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task SessionCleaner_SecurelyErasesProjectionDatabaseWalAndShmBytes()
    {
        const string canary = "history-cleaner-plaintext-canary-27db168e";
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Secure cleanup");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Secure cleanup", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, canary);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        await indexer.StopAsync();
        Assert.True(DatabaseFilesContain(projection.DatabasePath, canary));
        services.GetRequiredService<AgentLocalStore>().DeleteSessionTree(session.SessionId);

        indexer.DeleteSessionData(session.SessionId);

        Assert.Equal(0, projection.GetSnapshot().DocumentCount);
        AssertDatabaseFilesDoNotContain(projection.DatabasePath, canary);
    }

    [Fact]
    public void TranscriptRollback_SecurelyErasesAgentDatabaseWalAndShmBytes()
    {
        const string canary = "transcript-rollback-plaintext-canary-a864e91c";
        using var scope = RegressionTestPackageScope.Create();
        using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Secure rollback");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Secure rollback", workspaceId: workspace.WorkspaceId);
        var anchor = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "rollback anchor");
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, canary);
        var store = services.GetRequiredService<AgentLocalStore>();
        Assert.True(DatabaseFilesContain(store.DatabasePath, canary));

        var result = store.RollbackTranscript(session.SessionId, anchor.TurnId);

        Assert.Equal(2, result.DeletedTurnIds.Count);
        AssertDatabaseFilesDoNotContain(store.DatabasePath, canary);
    }

    [Fact]
    public async Task IdleIndexer_DoesNotChurnStatusRevision()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Idle");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "idle history");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var state = services.GetRequiredService<HistorySearchRuntimeState>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => state.Current.Availability == HistorySearchAvailability.Ready
                                   && state.Current.IndexedDocuments == 1
                                   && state.Current.PendingChanges == 0);
        var revision = state.Current.Revision;

        await Task.Delay(600);

        Assert.Equal(revision, state.Current.Revision);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task InPlaceIndexUpdateAdvancesProjectionRevisionAndNoOpReconciliationDoesNot()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Projection revision");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "first revision");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var state = services.GetRequiredService<HistorySearchRuntimeState>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => state.Current.Availability == HistorySearchAvailability.Ready
                                   && state.Current.IndexedDocuments == 1);
        var generation = state.Current.TextGeneration;
        var initialProjectionRevision = state.Current.ProjectionRevision;

        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "second revision");
        await WaitUntilAsync(() => state.Current.ProjectionRevision > initialProjectionRevision
                                   && state.Current.IndexedDocuments == 2);

        Assert.Equal(generation, state.Current.TextGeneration);
        var updatedProjectionRevision = state.Current.ProjectionRevision;
        await indexer.ReconcileNowAsync();
        Assert.Equal(updatedProjectionRevision, state.Current.ProjectionRevision);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task FailedTextRebuild_KeepsSafeGenerationAndRetriesAutomaticallyAfterCooldown()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Retry");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Retry session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "safe generation remains searchable");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        var state = services.GetRequiredService<HistorySearchRuntimeState>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        var safeGeneration = projection.GetSnapshot().ActiveTextGenerationId;
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectHistoryStagingInsert
                BEFORE INSERT ON HistoryDocuments
                WHEN NEW.GenerationId <> (SELECT ActiveTextGenerationId FROM HistoryProjectionState WHERE Id = 1)
                BEGIN
                    SELECT RAISE(ABORT, 'staging insert rejected');
                END;
                CREATE TRIGGER RejectHistoryStagingCleanup
                BEFORE DELETE ON HistoryProjectionGenerations
                WHEN OLD.State = 'Staging'
                BEGIN
                    SELECT RAISE(ABORT, 'staging cleanup rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        indexer.RequestRebuild();
        await WaitUntilAsync(() => state.Current.FailureCode == "text-rebuild-failed");

        Assert.Equal(safeGeneration, projection.GetSnapshot().ActiveTextGenerationId);
        Assert.Single((await search.SearchAsync(new HistorySearchRequest("safe generation"))).Results);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TRIGGER RejectHistoryStagingInsert;
                DROP TRIGGER RejectHistoryStagingCleanup;
                """;
            command.ExecuteNonQuery();
        }

        await WaitUntilAsync(() => projection.GetSnapshot().ActiveTextGenerationId != safeGeneration);
        Assert.Single((await search.SearchAsync(new HistorySearchRequest("safe generation"))).Results);
        Assert.Null(state.Current.FailureCode);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task FailedTextGenerationStart_RetriesAfterCooldownWithoutIdleRevisionSpin()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Begin retry");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Begin retry", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "begin-generation-retry-anchor");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var state = services.GetRequiredService<HistorySearchRuntimeState>();
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectHistoryGenerationBegin
                BEFORE INSERT ON HistoryProjectionGenerations
                BEGIN
                    SELECT RAISE(ABORT, 'generation begin rejected');
                END;
                """;
            command.ExecuteNonQuery();
        }

        await indexer.StartAsync();
        await WaitUntilAsync(() => state.Current.FailureCode == "text-rebuild-failed");
        Assert.Null(projection.GetSnapshot().ActiveTextGenerationId);
        var failedRevision = state.Current.Revision;

        await Task.Delay(250);

        Assert.Equal(failedRevision, state.Current.Revision);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "wake-failed-generation");
        Assert.True(indexer.GetNextWakeDelay() > TimeSpan.Zero);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectHistoryGenerationBegin;";
            command.ExecuteNonQuery();
        }

        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 2
                                   && state.Current.FailureCode is null);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task CancelledStop_StartDrainsOldWorkerAndDisposeDrainsReplacementBeforeGates()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Drain worker");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Drain worker", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "initial-drain-anchor");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var workerField = typeof(HistorySearchIndexingService).GetField(
            "_worker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var operationGateField = typeof(HistorySearchIndexingService).GetField(
            "_operationGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var lifetimeField = typeof(HistorySearchIndexingService).GetField(
            "_lifetime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var signalField = typeof(HistorySearchIndexingService).GetField(
            "_signal",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(workerField);
        Assert.NotNull(lifetimeField);
        Assert.NotNull(signalField);
        var operationGate = Assert.IsType<SemaphoreSlim>(operationGateField?.GetValue(indexer));
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1
                                   && operationGate.CurrentCount == 1);
        var oldWorker = Assert.IsAssignableFrom<Task>(workerField.GetValue(indexer));
        var oldLifetime = Assert.IsType<CancellationTokenSource>(lifetimeField.GetValue(indexer));
        var oldSignal = signalField.GetValue(indexer);
        Assert.NotNull(oldSignal);

        using (var blocker = Open(projection.DatabasePath))
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "blocked-restart-anchor");
            await WaitUntilAsync(() => operationGate.CurrentCount == 0);
            await Task.Delay(100);
            using var stopCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexer.StopAsync(stopCancellation.Token));
            var restart = indexer.StartAsync();
            await Task.Delay(100);
            Assert.False(restart.IsCompleted);
            Assert.Same(oldWorker, workerField.GetValue(indexer));
            Assert.Same(oldLifetime, lifetimeField.GetValue(indexer));
            Assert.Same(oldSignal, signalField.GetValue(indexer));

            transaction.Rollback();
            await restart.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 2
                                   && operationGate.CurrentCount == 1);
        var replacementWorker = Assert.IsAssignableFrom<Task>(workerField.GetValue(indexer));
        Assert.NotSame(oldWorker, replacementWorker);
        Assert.NotSame(oldLifetime, lifetimeField.GetValue(indexer));
        Assert.NotSame(oldSignal, signalField.GetValue(indexer));
        Assert.True(oldWorker.IsCompleted);

        using (var blocker = Open(projection.DatabasePath))
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "blocked-dispose-anchor");
            await WaitUntilAsync(() => operationGate.CurrentCount == 0);
            await Task.Delay(100);
            using var stopCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexer.StopAsync(stopCancellation.Token));
            var disposal = indexer.DisposeAsync().AsTask();
            await Task.Delay(100);
            Assert.False(disposal.IsCompleted);
            Assert.Same(replacementWorker, workerField.GetValue(indexer));

            transaction.Rollback();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(replacementWorker.IsCompleted);
        Assert.Null(workerField.GetValue(indexer));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(30, 60)]
    public void TextRebuildRetry_UsesBoundedExponentialCooldown(int failureCount, int expectedSeconds)
        => Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            HistorySearchIndexingService.GetTextRebuildRetryDelay(failureCount));

    [Fact]
    public async Task RuntimeSearch_RecoversPostStartupSchemaCorruptionWithoutPropagatingFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        await using var services = CreateRuntime(scope, catalog);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Recovery");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "recovery needle");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE HistoryDocumentsFts;";
            command.ExecuteNonQuery();
        }

        var response = await search.SearchAsync(new HistorySearchRequest("recovery"));

        Assert.Empty(response.Results);
        Assert.True(response.IsPartial);
        Assert.True(projection.WasRecovered);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(projection.DatabasePath)!,
            "history-search.db.corrupt-*"));
        await indexer.StopAsync();
    }

    [Fact]
    public async Task SessionDeletion_CoordinatesCorruptionRecoveryAndReactivatesSemanticFence()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Delete recovery");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Delete me", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "recover during deletion");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var fence = services.GetRequiredService<HistorySemanticOperationFence>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE HistoryDocumentsFts;";
            command.ExecuteNonQuery();
        }
        using var staleLease = fence.Begin(projection.GetConfiguration().Revision, CancellationToken.None);

        var deletion = Task.Run(() => indexer.DeleteSessionData(session.SessionId));
        await WaitUntilAsync(() => staleLease.CancellationToken.IsCancellationRequested);
        Assert.False(deletion.IsCompleted);
        staleLease.Dispose();
        await deletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(projection.WasRecovered);
        using var currentLease = fence.Begin(projection.GetConfiguration().Revision, CancellationToken.None);
        currentLease.ThrowIfCurrent();
        await indexer.StopAsync();
    }

    [Theory]
    [InlineData("DROP TABLE HistoryDocumentFacets;")]
    [InlineData("DELETE FROM HistoryProjectionState;")]
    [InlineData("ALTER TABLE HistoryDocuments RENAME COLUMN ProjectionHash TO BrokenHash;")]
    [InlineData("DROP TABLE HistoryDocumentsFts;")]
    public void ProjectionStartup_ValidatesTablesColumnsSingletonsAndFts(string corruptionSql)
    {
        using var scope = RegressionTestPackageScope.Create();
        var original = new HistorySearchStore(scope.Context);
        using (var connection = Open(original.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = corruptionSql;
            command.ExecuteNonQuery();
        }

        var recovered = new HistorySearchStore(scope.Context);

        Assert.True(recovered.IsAvailable);
        Assert.True(recovered.WasRecovered);
        Assert.Equal(HistorySearchVersions.Schema, ExecuteInt64(
            Open(recovered.DatabasePath),
            "SELECT Version FROM HistorySchemaMarker WHERE Id = 1;"));
    }

    [Fact]
    public void ProjectionStartup_RejectsInvalidVectorBlobInvariant()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = CreateReadyEmbeddingProjection(scope.Context);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE HistoryEmbeddings SET Vector = x'00';";
            command.ExecuteNonQuery();
        }

        var recovered = new HistorySearchStore(scope.Context);

        Assert.True(recovered.WasRecovered);
        Assert.Equal(0, recovered.GetSnapshot().EmbeddingCount);
    }

    [Theory]
    [InlineData(5, "database is locked")]
    [InlineData(6, "database table is locked")]
    [InlineData(9, "interrupted")]
    [InlineData(13, "database or disk is full")]
    [InlineData(14, "unable to open database file")]
    public void ProjectionRecovery_DoesNotClassifyTransientOrResourceSqliteFailuresAsCorruption(
        int errorCode,
        string message)
    {
        Assert.False(HistorySearchStore.IsConfirmedProjectionFailure(
            new SqliteException(message, errorCode)));
        Assert.False(HistorySearchStore.IsConfirmedProjectionFailure(
            new OperationCanceledException()));
    }

    [Fact]
    public void ProjectionRecovery_TransientFailuresPreserveConfigurationAndManualClearState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        projection.ChangeConfiguration(false, null, null, null, null);
        var cleared = projection.ClearDerivedIndex();

        Assert.False(projection.TryRecover(new SqliteException("database is locked", 5)));
        Assert.False(projection.TryRecover(new SqliteException("database or disk is full", 13)));
        Assert.False(projection.TryRecover(new OperationCanceledException()));

        Assert.True(projection.GetSnapshot().IsManuallyCleared);
        Assert.Equal(cleared.Revision, projection.GetConfiguration().Revision);
        Assert.False(projection.WasRecovered);
    }

    [Fact]
    public async Task SemanticReadiness_RequiresCompleteCoverageAndIsResetOnlyDuringOwnedStartup()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var textGeneration = projection.BeginTextGeneration();
        var first = CreateDocument("first");
        var second = CreateDocument("second") with
        {
            DocumentId = Guid.NewGuid().ToString("N"),
            TurnId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
        };
        projection.ReplaceTurnDocuments(textGeneration, first.SessionId, first.TurnId, [first]);
        projection.ReplaceTurnDocuments(textGeneration, second.SessionId, second.TurnId, [second]);
        projection.ActivateTextGeneration(textGeneration);
        var configuration = projection.ChangeConfiguration(
            true, "package.ready", "provider", "model", new string('c', 64));
        var embeddingGeneration = projection.BeginEmbeddingGeneration(textGeneration, configuration);
        var work = projection.ListEmbeddingWorkPage(textGeneration, null, 10);
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [1f, 0f]),
            "model",
            null,
            out var dimensions,
            out var vector));
        projection.SaveEmbedding(
            embeddingGeneration,
            textGeneration,
            configuration,
            work[0],
            dimensions,
            vector);

        Assert.Throws<InvalidOperationException>(() => projection.ActivateEmbeddingGeneration(
            embeddingGeneration,
            dimensions,
            configuration));
        Assert.False(projection.GetSnapshot().SemanticReady);

        projection.SaveEmbedding(
            embeddingGeneration,
            textGeneration,
            configuration,
            work[1],
            dimensions,
            vector);
        projection.ActivateEmbeddingGeneration(embeddingGeneration, dimensions, configuration);
        Assert.True(projection.GetSnapshot().SemanticReady);
        using (var connection = Open(projection.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM HistoryEmbeddings; UPDATE HistoryProjectionState SET SemanticReady = 1;";
            command.ExecuteNonQuery();
        }

        var candidate = new HistorySearchStore(scope.Context);
        Assert.True(candidate.GetSnapshot().SemanticReady);

        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var ownedProjection = services.GetRequiredService<HistorySearchStore>();
        Assert.True(ownedProjection.GetSnapshot().SemanticReady);
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        await indexer.StartAsync();

        Assert.False(ownedProjection.GetSnapshot().SemanticReady);
        Assert.False(ownedProjection.GetConfiguration().SemanticEnabled);
        await indexer.StopAsync();
    }

    [Fact]
    public void FailedGeneration_DeletesStagingRowsPromptly()
    {
        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        var generation = projection.BeginTextGeneration();
        var document = CreateDocument("failed generation");
        projection.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);

        projection.FailGeneration(generation, "expected");

        using var connection = Open(projection.DatabasePath);
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryDocuments WHERE GenerationId = {generation};"));
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryDocumentsFts WHERE CAST(GenerationId AS INTEGER) = {generation};"));
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {generation};"));
    }

    [Fact]
    public async Task CandidateConstruction_DoesNotReclaimAbandonedGenerationBeforeOwnedStartup()
    {
        using var scope = RegressionTestPackageScope.Create();
        var original = new HistorySearchStore(scope.Context);
        var generation = original.BeginTextGeneration();
        var document = CreateDocument("abandoned staging");
        original.ReplaceTurnDocuments(generation, document.SessionId, document.TurnId, [document]);

        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var candidate = services.GetRequiredService<HistorySearchStore>();

        Assert.False(candidate.WasRecovered);
        using (var preStartupConnection = Open(candidate.DatabasePath))
        {
            Assert.Equal(1L, ExecuteInt64(preStartupConnection, $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {generation};"));
            Assert.Equal(1L, ExecuteInt64(preStartupConnection, $"SELECT COUNT(*) FROM HistoryDocuments WHERE GenerationId = {generation};"));
            Assert.Equal(1L, ExecuteInt64(preStartupConnection, $"SELECT COUNT(*) FROM HistoryDocumentsFts WHERE CAST(GenerationId AS INTEGER) = {generation};"));
        }

        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        await indexer.StartAsync();
        using var connection = Open(candidate.DatabasePath);
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryProjectionGenerations WHERE GenerationId = {generation};"));
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryDocuments WHERE GenerationId = {generation};"));
        Assert.Equal(0L, ExecuteInt64(connection, $"SELECT COUNT(*) FROM HistoryDocumentsFts WHERE CAST(GenerationId AS INTEGER) = {generation};"));
        await indexer.StopAsync();
    }

    [Fact]
    public void SemanticScanCap_IsDimensionDependentAndNoNativeAnnSchemaIsCreated()
    {
        Assert.Equal(
            HistorySearchLimits.MaximumSemanticScanDocuments,
            HistorySearchLimits.GetMaximumSemanticScanDocuments(2));
        Assert.InRange(
            HistorySearchLimits.GetMaximumSemanticScanDocuments(HistorySearchLimits.MaximumVectorDimensions),
            1,
            HistorySearchLimits.MaximumSemanticScanDocuments - 1);
        Assert.Equal(0, HistorySearchLimits.GetMaximumSemanticScanDocuments(0));

        using var scope = RegressionTestPackageScope.Create();
        var projection = new HistorySearchStore(scope.Context);
        using var connection = Open(projection.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE '%vector%' OR lower(name) LIKE '%ann%';";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task StateCatalog_IsBoundedPaginatedAndFitsWorstCaseUnicodePayloads()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        await using var services = CreateRuntime(scope, catalog);
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var sessions = services.GetRequiredService<AgentSessionService>();
        var workspace = workspaces.CreateWorkspace("Unicode " + new string('\ud83d', 200));
        for (var index = 0; index < 45; index++)
        {
            sessions.CreateSession(
                $"{index:D2}-" + string.Concat(Enumerable.Repeat("😀\\\u0001", 200)),
                workspaceId: workspace.WorkspaceId);
        }
        var search = services.GetRequiredService<HistorySearchService>();

        var first = await search.GetStateAsync(new HistorySearchStateRequest(Limit: 20));
        var second = await search.GetStateAsync(new HistorySearchStateRequest(
            Continuation: first.Continuation,
            Limit: 20));

        Assert.Equal(20, first.Sessions.Count);
        Assert.Equal(20, second.Sessions.Count);
        Assert.NotNull(first.Continuation);
        Assert.Empty(first.Sessions.Select(option => option.Id).Intersect(second.Sessions.Select(option => option.Id)));
        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(first)
                    <= AgentRuntimePayloadLimits.MaximumOperationResponseBytes);
        Assert.All(first.Sessions, option => Assert.InRange(option.DisplayName.Length, 1, HistorySearchLimits.MaximumDisplayCharacters));
    }

    [Fact]
    public async Task ContinuationRestartsAcrossGenerationAndChildFilterIncludesFullDescendantClosure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        await using var services = CreateRuntime(scope, catalog);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Tree");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var root = sessions.CreateSession("Root", workspaceId: workspace.WorkspaceId);
        var child = sessions.CreateSession(
            "Child", root.SessionId, root.SessionId, workspaceId: workspace.WorkspaceId);
        var emptyIntermediate = sessions.CreateSession(
            "Empty intermediate", child.SessionId, root.SessionId, workspaceId: workspace.WorkspaceId);
        var grandchild = sessions.CreateSession(
            "Grandchild", emptyIntermediate.SessionId, root.SessionId, workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(child.SessionId, AgentMessageRole.User, "closure needle child");
        sessions.AppendTextTurn(grandchild.SessionId, AgentMessageRole.User, "closure needle grandchild");
        sessions.AppendTextTurn(root.SessionId, AgentMessageRole.User, "closure needle root");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 3);

        var descendants = await search.SearchAsync(new HistorySearchRequest(
            "closure needle",
            SessionId: child.SessionId,
            IncludeChildSessions: true));
        Assert.Equal(
            new[] { child.SessionId, grandchild.SessionId }.Order(),
            descendants.Results.Select(result => result.SessionId).Order());
        var childOnly = await search.SearchAsync(new HistorySearchRequest(
            "closure needle",
            SessionId: child.SessionId,
            IncludeChildSessions: false));
        Assert.Equal(child.SessionId, Assert.Single(childOnly.Results).SessionId);

        var firstPage = await search.SearchAsync(new HistorySearchRequest("closure needle", Limit: 1));
        var oldGeneration = projection.GetSnapshot().ActiveTextGenerationId;
        indexer.RequestRebuild();
        await WaitUntilAsync(() => projection.GetSnapshot().ActiveTextGenerationId != oldGeneration
                                   && projection.GetSnapshot().DocumentCount == 3);
        var restartedPage = await search.SearchAsync(new HistorySearchRequest(
            "closure needle",
            Limit: 1,
            Continuation: firstPage.Continuation));
        Assert.Equal(firstPage.Results[0].DocumentId, restartedPage.Results[0].DocumentId);
        Assert.True(restartedPage.IsPartial);
        Assert.True(restartedPage.Restarted);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task InPlaceDocumentMutation_InvalidatesOldRankingAndNeverReturnsReplacementContent()
    {
        using var scope = RegressionTestPackageScope.Create();
        await using var services = CreateRuntime(scope, new RegressionTestExtensionCatalog());
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Mutation");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "mutable ranking needle first");
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "mutable ranking needle second");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 2);
        var firstPage = await search.SearchAsync(new HistorySearchRequest("mutable ranking needle", Limit: 1));
        var replaced = Assert.Single(firstPage.Results);

        sessions.UpdateTextTurn(replaced.TurnId, "replacement content without the old terms");
        await WaitUntilAsync(() => projection.SearchLexical(
            projection.GetSnapshot().ActiveTextGenerationId!.Value,
            new HistorySearchRequest("mutable ranking needle"),
            10).Count == 1);
        var resumed = await search.SearchAsync(new HistorySearchRequest(
            "mutable ranking needle",
            Limit: 1,
            Continuation: firstPage.Continuation));

        Assert.True(resumed.Restarted);
        Assert.DoesNotContain(
            resumed.Results,
            result => result.Snippet.Contains("replacement content", StringComparison.Ordinal));
        await indexer.StopAsync();
    }

    [Fact]
    public async Task AuthoritativeDatabaseSchemaFailure_DoesNotRecoverOrDeleteProjectionState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        await using var services = CreateRuntime(scope, catalog);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Authority");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var session = sessions.CreateSession("Session", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "authority failure needle");
        var indexer = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var search = services.GetRequiredService<HistorySearchService>();
        await indexer.StartAsync();
        await WaitUntilAsync(() => projection.GetSnapshot().DocumentCount == 1);
        var generation = projection.GetSnapshot().ActiveTextGenerationId;
        var configuration = projection.GetConfiguration();
        using (var connection = Open(services.GetRequiredService<AgentLocalStore>().DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE AgentTurnItems;";
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<SqliteException>(() => search.SearchAsync(
            new HistorySearchRequest("authority failure needle")));

        Assert.False(projection.WasRecovered);
        Assert.Equal(generation, projection.GetSnapshot().ActiveTextGenerationId);
        Assert.Equal(configuration.Revision, projection.GetConfiguration().Revision);
        await indexer.StopAsync();
    }

    [Fact]
    public async Task TranscriptAnchorHandlers_RejectWrongSessionRoleAndGatewayIsSeparated()
    {
        Assert.DoesNotContain(
            typeof(IAgentHistorySearchGateway).GetMethods(),
            method => method.Name.Contains("Transcript", StringComparison.Ordinal));
        Assert.Contains(
            typeof(IAgentTranscriptAnchorGateway).GetMethods(),
            method => method.Name == nameof(IAgentTranscriptAnchorGateway.LoadTranscriptAroundTurnAsync));

        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        await using var services = CreateRuntime(scope, catalog);
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Navigation");
        var sessions = services.GetRequiredService<AgentSessionService>();
        var root = sessions.CreateSession("Root", workspaceId: workspace.WorkspaceId);
        var child = sessions.CreateSession("Child", root.SessionId, root.SessionId, workspaceId: workspace.WorkspaceId);
        var rootTurn = sessions.AppendTextTurn(root.SessionId, AgentMessageRole.User, "root");
        var childTurn = sessions.AppendTextTurn(child.SessionId, AgentMessageRole.User, "child");
        var handler = services.GetRequiredService<AgentTranscriptAroundTurnHandler>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.HandleAsync(
            new AgentTranscriptAroundTurnRequest(child.SessionId, childTurn.TurnId)).AsTask());
        var runtime = services.GetRequiredService<AgentRuntimeCatalog>();
        Assert.Throws<InvalidOperationException>(() => SubsessionLocalRuntimeAdapter.BuildAroundTurnPage(
            runtime,
            root.SessionId,
            rootTurn.TurnId,
            rootTurn.CreatedAtUtc,
            rootTurn.Items[0].ItemId,
            10,
            10));
    }

    [Fact]
    public void AgentDatabaseMigrationLedger_IsAppendOnlyThroughVersion21()
    {
        using var scope = RegressionTestPackageScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var connection = Open(store.DatabasePath);
        Assert.Equal(21L, ExecuteInt64(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(21L, ExecuteInt64(connection, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.Equal(
            "durable-resource-claims|849bc5010a9c700c8cb683e2992e442dc4471fed275135b39f60ae8d54f46fa0",
            ExecuteString(connection, "SELECT Name || '|' || Checksum FROM SchemaMigrations WHERE Version = 20;"));
        Assert.Equal(
            "legacy-tool-execution-identities|14347bc760494c9e2b7691278989f71e8f6e37ceb02ab0f5e98c7d583dee29ce",
            ExecuteString(connection, "SELECT Name || '|' || Checksum FROM SchemaMigrations WHERE Version = 21;"));
    }

    private static ServiceProvider CreateRuntime(
        RegressionTestPackageScope scope,
        RegressionTestExtensionCatalog catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(scope.Context);
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<IPackageExtensionCatalog>(catalog);
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        new PackageModule().ConfigureRuntimeServices(services, scope.Context);
        return services.BuildServiceProvider();
    }

    private static HistorySearchStore CreateReadyEmbeddingProjection(IPackageContext context)
    {
        var projection = new HistorySearchStore(context);
        var textGeneration = projection.BeginTextGeneration();
        var document = CreateDocument("ready embedding");
        projection.ReplaceTurnDocuments(textGeneration, document.SessionId, document.TurnId, [document]);
        projection.ActivateTextGeneration(textGeneration);
        var configuration = projection.ChangeConfiguration(
            true, "package.ready", "provider", "model", new string('d', 64));
        var embeddingGeneration = projection.BeginEmbeddingGeneration(textGeneration, configuration);
        var work = Assert.Single(projection.ListEmbeddingWorkPage(textGeneration, null, 10));
        Assert.True(HistoryVectorCodec.TryNormalize(
            new AgentEmbeddingGenerationResult("model", [1f, 0f]),
            "model",
            null,
            out var dimensions,
            out var vector));
        projection.SaveEmbedding(
            embeddingGeneration,
            textGeneration,
            configuration,
            work,
            dimensions,
            vector);
        projection.ActivateEmbeddingGeneration(embeddingGeneration, dimensions, configuration);
        return projection;
    }

    private static HistoryProjectionDocument CreateDocument(string body, string? path = null)
    {
        var turnId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        return new HistoryProjectionDocument(
            HistorySearchExtractor.CreateDocumentId(itemId, 0, HistoryAnchorKind.Text),
            "workspace",
            "Workspace",
            Guid.NewGuid(),
            "Session",
            null,
            null,
            "profile",
            AgentMessageRole.User,
            HistoryActivityKind.None,
            turnId,
            itemId,
            null,
            HistoryAnchorKind.Text,
            1,
            false,
            DateTimeOffset.UtcNow,
            body,
            body,
            path is null ? [] : [new HistoryProjectionFacet("path", path, path.ToLowerInvariant())]);
    }

    private static HistorySourceSession CreateSourceSession()
        => new(
            Guid.NewGuid(),
            "Session",
            "workspace",
            "Workspace",
            null,
            null,
            "profile",
            "Profile",
            DateTimeOffset.UtcNow);

    private static AgentTurnRecord CreateToolTurn(
        string toolId,
        string argumentsJson,
        AgentToolExecutionStatus? status = null,
        string? ownerPackageId = null)
    {
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolCall,
            null,
            "call-1",
            toolId,
            argumentsJson,
            null,
            null,
            null,
            false,
            false,
            null,
            null)
        {
            ToolExecutionStatus = status,
            ToolOwnerPackageId = ownerPackageId,
        };
        return new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            [item],
            now,
            now)
        {
            ContentRevision = 1,
        };
    }

    private static AgentToolExecutionRecord PrepareHistoryToolExecution(
        AgentLocalStore store,
        AgentSessionRecord session,
        string toolId,
        string argumentsJson,
        bool isReadOnly,
        string? ownerPackageId,
        string callId = "call-1")
    {
        var run = store.ReserveRun(session.SessionId, "history.profile", "history tool call");
        var running = Assert.IsType<AgentRunTransitionResult>(store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Running history provenance test."));
        return Assert.Single(store.TryPrepareToolExecutions(
            run.Key,
            running.Run.Epoch,
            [new AgentToolExecutionPreparation(
                new AgentToolCallRequest(callId, toolId, argumentsJson),
                isReadOnly,
                AgentToolInvocationFingerprint.Create(toolId, argumentsJson),
                ownerPackageId)])!).Execution;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ExecuteString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static bool DatabaseFilesContain(string databasePath, string canary)
    {
        var bytes = Encoding.UTF8.GetBytes(canary);
        return new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .Any(path => File.ReadAllBytes(path).AsSpan().IndexOf(bytes) >= 0);
    }

    private static void AssertDatabaseFilesDoNotContain(string databasePath, string canary)
        => Assert.False(
            DatabaseFilesContain(databasePath, canary),
            $"Database files retained plaintext canary '{canary}'.");

    private static async Task WaitForSearchReadinessAsync(
        HistorySearchStore projection,
        HistorySearchRuntimeState runtimeState,
        HistorySearchService search,
        string query,
        int expectedDocumentCount,
        TimeSpan? timeout = null)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(30);
        using var deadlineCancellation = new CancellationTokenSource(deadline);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        void OnStatusChanged(HistorySearchStatus _) => changes.Writer.TryWrite(true);

        var lastResultCount = -1;
        runtimeState.Changed += OnStatusChanged;
        try
        {
            while (true)
            {
                var snapshot = projection.GetSnapshot();
                var pin = projection.TryPinActiveProjection();
                var status = runtimeState.Current;
                if (snapshot.ActiveTextGenerationId is { } activeGeneration
                    && pin?.TextGenerationId == activeGeneration
                    && snapshot.DocumentCount == expectedDocumentCount
                    && snapshot.LastReconciledAtUtc is not null
                    && status.Availability == HistorySearchAvailability.Ready
                    && status.TextGeneration == activeGeneration
                    && status.IndexedDocuments == expectedDocumentCount
                    && status.PendingChanges == 0
                    && status.LastReconciledAtUtc == snapshot.LastReconciledAtUtc)
                {
                    var response = await search.SearchAsync(
                        new HistorySearchRequest(query),
                        deadlineCancellation.Token);
                    lastResultCount = response.Results.Count;
                    if (!response.IsPartial
                        && response.Status.Availability == HistorySearchAvailability.Ready
                        && response.Status.TextGeneration == activeGeneration
                        && response.Results.Count == 1)
                    {
                        return;
                    }
                }

                await changes.Reader.ReadAsync(deadlineCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            var snapshot = projection.GetSnapshot();
            var status = runtimeState.Current;
            throw new TimeoutException(
                $"History Search did not become ready within {deadline}. "
                + $"Projection generation={snapshot.ActiveTextGenerationId?.ToString() ?? "none"}, "
                + $"status generation={status.TextGeneration?.ToString() ?? "none"}, "
                + $"availability={status.Availability}, projection documents={snapshot.DocumentCount}, "
                + $"status documents={status.IndexedDocuments}, pending={status.PendingChanges}, "
                + $"projection reconciled={snapshot.LastReconciledAtUtc?.ToString("O") ?? "never"}, "
                + $"status reconciled={status.LastReconciledAtUtc?.ToString("O") ?? "never"}, "
                + $"last search results={lastResultCount}.");
        }
        finally
        {
            runtimeState.Changed -= OnStatusChanged;
            changes.Writer.TryComplete();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for history state.");
            }
            await Task.Delay(20);
        }
    }

    private abstract class FingerprintProvider(
        string providerId,
        string modelId,
        string? displayName = null) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(providerId, displayName ?? providerId, []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([
                new(modelId, modelId, 2),
            ]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                providerId,
                AgentProviderReadinessStatus.Ready,
                "Ready"));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string requestedModelId,
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new(requestedModelId, [1f, 0f]));

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string requestedModelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(
                texts.Select(_ => (AgentEmbeddingGenerationResult?)new(requestedModelId, [1f, 0f])).ToArray());
    }

    private sealed class FirstFingerprintProvider(string providerId, string modelId)
        : FingerprintProvider(providerId, modelId);

    private sealed class SecondFingerprintProvider(string providerId, string modelId)
        : FingerprintProvider(providerId, modelId);

    private sealed class DisplayNameFingerprintProvider(
        string providerId,
        string modelId,
        string displayName)
        : FingerprintProvider(providerId, modelId, displayName);

    private sealed class ConfigurableFingerprintProvider(string providerId, string modelId)
        : FingerprintProvider(providerId, modelId), IAgentEmbeddingSpaceIdentityProvider
    {
        internal string SpaceIdentity { get; set; } = string.Empty;

        public ValueTask<string> GetEmbeddingSpaceIdentityAsync(
            string requestedModelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SpaceIdentity);
        }
    }

    private sealed class CatalogTool(string toolId) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(toolId, toolId, "Test tool");

        public ValueTask<AgentToolReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(
                toolId,
                AgentToolReadinessStatus.Ready,
                "Ready"));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(toolId, "Not used"));
    }

    private sealed class BlockingEmbeddingProvider : IAgentEmbeddingProvider
    {
        internal const string ProviderId = "blocking-history";
        internal const string ModelId = "blocking-v1";

        internal TaskCompletionSource BatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(ProviderId, "Blocking", []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new(ModelId, "Blocking", 2)]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready"));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new(modelId, [1f, 0f]));

        public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            BatchStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ReadinessEmbeddingProvider :
        IAgentEmbeddingProvider,
        IAgentEmbeddingSpaceIdentityProvider
    {
        internal const string ProviderId = "readiness-history";
        internal const string ModelId = "Readiness-Model";

        internal AgentProviderReadinessStatus Status { get; set; } = AgentProviderReadinessStatus.Ready;
        internal int CallCount { get; private set; }

        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(ProviderId, "Readiness", []);

        public ValueTask<string> GetEmbeddingSpaceIdentityAsync(
            string modelId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult("readiness-space");
        }

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([
                new(ModelId, "Readiness", 2),
            ]);
        }

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new AgentEmbeddingProviderReadiness(
                ProviderId,
                Status,
                Status == AgentProviderReadinessStatus.Ready ? "Ready" : "Unavailable"));
        }

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId,
            string text,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new(modelId, [1f, 0f]));
        }

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(
                texts.Select(_ => (AgentEmbeddingGenerationResult?)new(modelId, [1f, 0f])).ToArray());
        }
    }
}
