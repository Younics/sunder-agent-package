# MCP And OAuth

`sunder.package.agent.mcp` turns configured Model Context Protocol servers into dynamic Agent tool groups. Most users add an MCP server as configuration, not as a new compiled Agent extension. Build a separate package only when additional policy, presentation, or non-MCP capability is required.

## Runtime Composition

The first-party MCP package registers:

- `IAgentNativeToolSource` through `ToolSources`.
- `IAgentProfileSelectableCapabilityProvider` for one selectable tool group per server.
- `IAgentSessionDataCleaner` for session-scoped connections.
- Package-specific Runtime operations/stream and an App settings client.
- A host callback handler for OAuth.

An MCP server is advertised only when it is enabled, selected on the active profile, discoverable, and used from an active session. Connections are scoped to session/workspace where appropriate and cleaned when the session is deleted.

## Server Configuration

The settings editor accepts one bare server object, not an outer `mcp` or `$schema` wrapper.

### Local Stdio

```json
{
  "type": "local",
  "enabled": true,
  "command": ["npx", "-y", "@modelcontextprotocol/server-everything"],
  "workingDirectory": "/path/to/project",
  "discoveryTimeout": 5000,
  "toolTimeout": 180000,
  "env": {
    "MY_API_KEY": "replace-in-settings"
  }
}
```

`command` must be a non-empty array of argument segments. The first segment is resolved as an executable; later segments remain separate arguments. Environment values are moved to package secret storage. A local server process runs with the Runtime host user's authority and is not sandboxed by MCP.

### Remote HTTP

```json
{
  "type": "remote",
  "enabled": true,
  "url": "https://mcp.example.com",
  "discoveryTimeout": 5000,
  "toolTimeout": 180000,
  "headers": {
    "Authorization": "Bearer replace-in-settings"
  }
}
```

Remote transport uses SDK auto-detection for supported HTTP modes, including streamable HTTP/SSE behavior. Header values are stored as package secrets; only their names remain in normal package state.

`timeout` is accepted as a legacy fallback for both phases. Current defaults are 15 seconds for discovery and 120 seconds for a tool call; effective values are capped at 120 seconds and 30 minutes respectively.

## Transport Security

- Remote endpoints must be absolute HTTP or HTTPS URLs.
- Endpoint user-info credentials are rejected. Use secret-backed headers or OAuth.
- A non-loopback endpoint carrying OAuth, headers, or query credentials must use HTTPS.
- Plain HTTP without credentials is still unauthenticated/integrity-unsafe and should be limited to deliberate development scenarios.
- Redirect, DNS, proxy, and server trust remain security-sensitive. Do not put credentials in logs or tool results.
- Local commands and imported configurations are trusted executable configuration. Review before enabling.

## Tool Discovery And Identity

Each configured server becomes a profile-selectable `tool-group`. Discovered tools receive a stable encoded id containing immutable server id plus MCP tool name. Legacy `<server-name>_<tool-name>` aliases are accepted only when they resolve unambiguously.

Discovery rejects:

- More than 256 tools from one server.
- Empty, duplicate, or case-colliding tool names.
- More than 1 MiB of aggregate name/title/description/schema metadata.

MCP tool result JSON is bounded to 4 MiB and at most 1,024 content items. Oversized data is omitted/truncated and marked. Tool arguments pass the shared Agent JSON byte/depth/property/case-collision validation.

The MCP protocol's tool metadata is not treated as sufficient local mutation authority. Discovered MCP descriptors are conservatively marked `IsReadOnly = false`, so the generic Agent mutation permission defaults to `Ask`. A remote server cannot self-declare its way around local Agent approval.

## OAuth Configuration

OAuth is available only for remote servers. Enable it with a boolean:

```json
{
  "type": "remote",
  "url": "https://mcp.example.com",
  "oauth": true
}
```

Or provide scopes and an existing public client id:

```json
{
  "type": "remote",
  "url": "https://mcp.example.com",
  "oauth": {
    "enabled": true,
    "scopes": ["tools.read", "tools.execute"],
    "clientId": "sunder-client"
  }
}
```

When no client id is configured, the MCP SDK may use dynamic client registration. Token caches, dynamic registration responses, and any explicit client secret are stored under server-scoped package secret keys. Clearing authorization deletes all three.

## Host-Owned Callback Flow

Interactive OAuth uses the registered callback handler id `mcp.oauth.v1`:

1. The App asks the host to start a callback session for a server id.
2. The host supplies the callback session id and exact redirect URI.
3. Runtime starts one server-scoped OAuth flow and returns the authorization URI.
4. App opens that URI through the host and polls the generic callback session.
5. The host receives the browser callback and routes query values to Runtime.
6. Runtime validates the active session, completes token exchange, and persists secrets.

Packages never bind callback ports. The OAuth service rejects an SDK-provided redirect URI that differs from the host-owned URI, rejects multiple redirects for one flow, and allows only one in-progress flow per server. Normal MCP connections are non-interactive; if cached authorization is absent/expired, the user must start authorization from settings instead of a background tool call opening a browser.

Do not put OAuth state, authorization codes, tokens, client secrets, or complete callback URLs in diagnostics.

## Failure And Recovery

| Symptom | Behavior/remediation |
| --- | --- |
| Server disabled or not profile-selected | No tools are advertised. Enable and select the server. |
| Discovery timeout/failure | Cached tools may be used; a bounded background refresh is queued. Status explains the unavailable server. |
| Tool timeout/server error | Tool returns a stable MCP error result; caller cancellation still propagates. |
| OAuth required with no cache | Authorize from MCP settings; tool discovery cannot start interactive auth. |
| Configuration secret missing | Enabled server save/readiness fails rather than launching with a blank credential. |
| Server deleted or OAuth disabled | Connections close and superseded header/environment/OAuth secrets are cleaned. |
| Session deleted | Session-scoped MCP connections are removed through `SessionDataCleaners`. |

## Extension Guidance

- Do not reference internal `ConfiguredMcpServerRecord`, connection manager, or OAuth service from another package.
- To expose an ordinary external API, prefer a purpose-built `IAgentTool`/`IAgentToolSource` with a specific permission surface.
- To support another protocol transport, propose a public contract change rather than registering a second MCP source with colliding identity.
- Reuse Sunder SDK callbacks for browser authorization; do not run a package-owned listener.
- Stack export must describe server transport/configuration separately from secret inputs. Never embed credentials in an exported stack.

First-party tests include `McpConfigurationDocumentTests`, `McpCallbackFlowTests`, `McpServerStackContributorTests`, `McpRefactorTests`, and `AgentRuntimeTransportLimitTests` under `tests/Sunder.Package.Agent.Tests`.
