# Agent web search

The Web Tools package exposes `web_search` using Exa and Parallel's hosted MCP
services. Both support limited anonymous search without an API key. Free search
remains subject to each service's quotas; it is not unlimited.

## Settings

| Setting | Default | Behavior |
| --- | --- | --- |
| `search.provider` | `auto` | `auto`, `exa`, or `parallel` |
| `search.maxResults.default` | `5` | Maximum returned sources, clamped to 1–10 |
| `search.exa.apiKey` | None | Optional secret for Exa searches |
| `search.parallel.apiKey` | None | Optional secret sent as a Bearer token to Parallel |

Auto prefers Exa when an Exa API key is configured; otherwise it starts with
Parallel. When a provider returns HTTP 429 or Exa's recognized MCP quota
diagnostic, Auto tries the other provider once. Explicit Exa/Parallel selections
stay pinned and report rate limiting instead of switching providers.

Cooldowns are shared by searches in the same active Web Tools worker. They honor
`Retry-After` (seconds or an HTTP date), defaulting to 60 seconds when absent or
invalid. Changing a provider's API key starts fresh credential-specific accounting.
If both providers are cooling down, searches fail immediately with
`web-search-rate-limited`. Authentication errors, malformed responses, other HTTP
failures, and caller cancellation do not trigger failover.

Parallel receives the host-owned conversation identifier, stable across searches
within a conversation. Results carry the actual backend ID, source links, and
bounded excerpts. Exa quota guidance is an error, never successful search content.

Provider references:

- [Exa MCP](https://github.com/exa-labs/exa-mcp-server)
- [Parallel Search MCP](https://docs.parallel.ai/integrations/mcp/search-mcp)
