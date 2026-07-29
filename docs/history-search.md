# History Search

History Search is an App-facing, search-first view backed by a disposable Runtime projection of the authoritative local Agent transcript. Opening the view immediately shows recent history for the current workspace. Entering a query searches local conversation text and safe activity metadata; History Search is not exposed as a model tool or memory source. Search work is shown by a reserved two-pixel line immediately below the query field, so initial loading, debounce, search, and paging never resize the input or shift the page.

## Local-Only Search

The shipped search modes are lexical text, path, and activity search. SQLite FTS5 and projection metadata run entirely inside Agent Runtime. History Search does not discover embedding models, create embeddings, call an embedding provider in the background, or send a query to a provider.

Semantic History Search contracts remain dormant for possible future use. As the final owned Agent startup step, Runtime fences and drains semantic work created by that package session, disables any persisted legacy semantic History Search configuration, and removes derived embedding generations before publishing lexical state. A replacement candidate does not cancel a remote call owned by the pre-existing package session: configuration revision checks reject stale writes, and normal post-swap shutdown requests cooperative cancellation from the retired session. This is independent from the separately installed Semantic Memory package.

An empty query is a recent-history request. The default scope is the workspace currently selected in Agent Chat. Advanced filters can switch to all workspaces or one specific workspace and can narrow by session, descendant sessions, profile, role, activity, and time. Advanced metadata and additional session pages load only when Advanced is opened, so the first recent or lexical request does not wait for the complete filter catalog.

Advanced session paging pins an `AgentSessions.rowid` insertion high-water mark in its continuation, excludes sessions inserted after that snapshot even when their timestamps are equal or backdated, rejects malformed or oversized cursors by restarting safely, and has fixed page and retained-option caps in the App. This prevents a live keyset from omitting newly inserted sessions behind its cursor and prevents an unbounded Runtime call or memory loop.

## Storage Boundary

The authoritative transcript remains in Runtime-owned `agent/agent.db`. Agent schema migration 16, `tool-execution-provenance`, adds nullable execution-time owner and optional schema provenance to the durable tool ledger. Existing null-owner executions are never backfilled from the current package catalog.

Runtime maintains the derived projection at `agent/history-search.db`. The App accesses it only through private package Runtime operations and a bounded status stream. App composition neither resolves `AgentLocalStore` nor opens either database.

The projection can be deleted at any time. Runtime recreates and reconciles it automatically from authoritative history. Unsupported or corrupt projection files and their WAL/SHM sidecars are deleted and replaced without retaining another plaintext copy or blocking Agent Runtime startup.

Deleting a session removes its authoritative transcript and its History Search projection rows. Deleting a workspace deletes its sessions and therefore removes both the source history and derived search projection. Index maintenance never restores deleted source data.

## Indexed Content

The text projection includes:

- Sanitized text items authored by users and assistants.
- Safe, allowlisted metadata from exact recognized tool IDs whose durable execution owner is the expected first-party package, including paths, basenames, sanitized URLs, operations, working directories, and execution status.
- `apply_patch` operation and path headers, never patch hunks.
- Minimal tool identity and execution status for unknown tools, never arbitrary argument payloads.

The projection excludes shell commands, search patterns, tool-result bodies, attachments, repository file contents, prompts, memory records, and arbitrary structured payloads. Credential-shaped values, bearer tokens, PEM blocks, sensitive assignments, multiline quoted values, and YAML block or indented credential values are removed before text, snippets, or facets are stored. YAML tags and anchors before quoted, block, or plain scalars, chomping and indentation indicators, doubled single-quote escapes, and indented plain-scalar continuations are handled without exposing their scalar content. Unterminated or malformed credential values fail closed while an unrelated next YAML property is retained when its indentation and key boundary can be identified reliably. Completed PEM blocks are redacted, and sensitive unterminated PEM material is consumed through EOF or a reliable enclosing YAML-property boundary.

Every ordinary HTTP(S) URL in stored or displayed text passes through the same sanitizer, not only URLs extracted from trusted tool arguments. User information, query strings, and fragments are removed while the safe origin, port, and path remain visible. Quoted or backtick-delimited private values are consumed with their query, fragment, or user-information component rather than left behind as orphaned text. Malformed HTTP(S) candidates fail closed as `[REDACTED URL]`. This boundary runs before general credential redaction so private URL canaries cannot cause neighboring safe text to be consumed as an assignment.

Runtime applies the same scalar repair, NFC normalization, credential redaction, and scalar-safe bounds again to human-readable output such as snippets, labels, facets, match reasons, and failure messages. Useful nonsecret label prefixes remain visible while serialized state and hits cannot expose a credential canary from authoritative display metadata. Opaque workspace, profile, provider, model, call, parent, runtime, document, and continuation identities bypass text normalization and redaction so they round-trip exactly.

## Matching And Filters

SQLite FTS5 uses Unicode tokenization with diacritic removal. Runtime first repairs invalid UTF-16, normalizes to NFC, applies the same invariant scalar fold to indexed text and queries, and tokenizes Unicode scalar values. Punctuation and metadata separators split tokens. Duplicate terms and all generated grammar are bounded. A nonempty query containing no searchable tokens returns no matches; it is never reinterpreted as a recent-history request.

Closed quoted spans are required strict phrases. Unmatched quotes are separators rather than FTS syntax. Unquoted terms produce independent, parameterized FTS lanes for an exact phrase, all exact terms, prefix-aware all terms, any exact term, and prefix-aware any term. The final unquoted token receives a token-start prefix automatically when it has more than one scalar; an explicit trailing `*` can prefix any unquoted token, including a one-scalar token. Prefixes never perform infix matching, so `occur` can find `occurred` and `occurrence` but not `reoccur`.

Each unique lane executes independently with a bounded BM25 result set. Exact facet and phrase evidence forms the strongest tier, all-term and facet-prefix evidence forms the next tier, and any-term evidence provides broad recall. Within a tier, weighted reciprocal-rank scores accumulate across every lane that found a document. Final ordering is tier, cumulative score, existing recency policy, and document ID as a total tie-break; equal-score ordering is tested directly rather than inferred from lane ranks that are normally unique. At most 400 ranked candidates proceed to authoritative validation.

Match reasons retain Runtime ranking provenance and distinguish phrase, all-term, any-term, path, activity, exact, and prefix evidence. `Recent history` remains internal provenance for an empty query and is not rendered as a result tag. Text hits use a match-centered FTS5 `snippet()` from a trusted broad body lane; Runtime redacts the result again and truncates only at a Unicode scalar boundary. Metadata-only hits use the already-redacted stored snippet.

Filters cover workspace, session, the full optional descendant closure, profile, user/assistant role, activity kind, and UTC date range. Results use deterministic bounded v2 continuation tokens that pin the active text generation, canonical request fingerprint, query-plan version, ranking version, and complete lane-rank identity. Fingerprints use structured canonical encoding rather than delimiter-joined text. Older or version-mismatched tokens restart safely from the first page. Every candidate is checked against the current authoritative turn revision and item/call identities before it is returned. Stale candidates are suppressed immediately and queue an authoritative session reindex.

Time presets are evaluated once for each new query or filter search and reused unchanged by every continuation page. Today starts at local midnight; rolling 7-day, 30-day, and one-year presets end at that search's snapshot instant. Custom dates include each selected local calendar day. A reversed custom range is visibly reported and normalized from the earlier date through the later date.

## Automatic Maintenance And Recovery

Runtime subscribes to transcript and workspace mutations before startup recovery can publish changes. Initial startup reconciles authoritative history automatically. Replacement builds use a staging text generation, replay concurrent mutations, and activate atomically. The committed Runtime epoch is persisted in projection state and generations; every mutating transaction verifies that exact durable owner both before mutation and again before commit. A paused worker from an expired epoch therefore cannot activate staging data or overwrite a replacement owner after takeover. If the bounded pending-key set overflows, Runtime records a full-reconciliation request and scans the complete authoritative snapshot after activation instead of advancing past dropped events. Requests arriving during that repair remain armed for another pass, so inserts, updates, and deletions are not lost. Streaming changes are coalesced with a short debounce; finalized turns, session metadata changes, transcript resets, workspace renames, and deletions are processed promptly. A Runtime-only session-data cleaner physically purges deleted history with SQLite and FTS secure deletion followed by a truncated WAL checkpoint, while periodic watermark slices repair missed notifications and remove sessions deleted while indexing was stopped.

If text-generation creation or a later rebuild step fails, the staging generation is discarded when one exists and an existing safe active generation remains searchable. Runtime retries automatically with bounded exponential cooldown, and maintenance timers remain dormant while no active generation exists, avoiding a hot loop. Successful activation resets the retry schedule. Lifecycle transitions are serialized: a `StartAsync` following a caller-canceled `StopAsync` must drain the retained worker before replacing its lifetime or signal, and disposal drains the captured worker before synchronization primitives are released. A legacy manually-cleared projection is treated as disposable startup state: Runtime clears that flag and rebuilds or reconciles authoritative history without requiring a command.

The projection uses SQLite write-ahead logging so active-generation reads continue during staging writes. Search and indexing operations drain before a schema or redaction provenance upgrade builds a complete replacement database, checkpoints it, and atomically replaces `history-search.db`; the prior database, WAL, and shared-memory sidecar are not retained. FTS5 secure-delete is enabled when supported, with full disposable-database recreation as the fallback. After a replacement generation activates successfully, superseded rows are discarded to keep the disposable database bounded. Corruption recovery recreates only derived storage; it never mutates authoritative transcripts. Authoritative transcript rollback transactions independently enable SQLite secure deletion and truncate any WAL after commit.

The UI has no Search, Rebuild, Clear index, or semantic configuration actions. Property changes search with a debounce, Enter searches immediately, and index maintenance is automatic. Status text reports preparation, compact indexed counts, background updates, automatic recovery, and separate search or navigation errors without instructing users to rebuild.

Results are full-width, rounded chat-inspired surfaces rather than divided list rows. User messages use the soft information surface with an accent border, assistant messages use the base surface, and activities use the code surface. Role and time are restrained context above the primary snippet. Paths are aggregated into labeled monospace metadata with ellipsis and full tooltips; session, workspace, and child-session context stays in a muted footer. Micro (under 240 px), compact (under 520 px), intermediate, and wide layouts retain the same full-width hierarchy without horizontal scrolling.

## Navigation

Each result carries workspace, session, turn, item, optional call, anchor kind, and timestamp identities. Root-session results open Agent Chat; child-session results open Subsessions. Runtime loads a bounded page around the authoritative anchor, and the shared transcript timeline starts detached from latest with both older and newer paging state. After layout, the exact text or activity row is placed near the top of the viewport and highlighted temporarily. If the source was deleted, the destination reports that the anchor is no longer available without removing the result list.

## Limits

Queries, filters, snippets, facets, result counts, continuation tokens, Runtime payloads, and pending mutation keys are bounded in code. Projection writes use generation-local transactions, and active generations remain searchable while replacement generations are built.
