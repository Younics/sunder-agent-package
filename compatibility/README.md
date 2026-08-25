# Agent Compatibility Freeze

`agent-compatibility-candidate.json` is the generated Agent 2.0 compatibility
ledger. It is derived from source declarations, descriptor bytes, package
metadata, release locks, public API files, live SQLite schemas, document schema
policies, configuration/environment/telemetry vocabulary, and the checked-in
public release evidence.

Regenerate it only after reviewing the intended compatibility delta:

```sh
./scripts/compatibility/update-agent-compatibility-candidate.sh --write
./scripts/compatibility/update-agent-compatibility-candidate.sh --check
```

Normal test runs regenerate the same bytes in memory and fail when the checked-in
candidate differs. The generator also asserts the architectural inventory:

- 31 Agent RPC descriptors: 19 public bundled descriptors and 12 package-local
  owner descriptors;
- descriptor schema version 2 and current Agent contract semantic version
  `1.0.0`;
- 18 supported third-party extension points plus the reserved first-party
  `sunder.agent.tool.execution.observed` bridge;
- 63 provider declarations and 46 consumer declarations;
- 15 Worker V3 runtime package families plus the Protocol NuGet artifact.

All 19 central Protocol bindings explicitly generate public DTO, provider,
client, and handler types. This is intentional extension-authoring API: external
providers and consumers need the complete generated vocabulary. All 12
package-local/owner bindings generate internal top-level types. Their worker,
App, and test consumers use maintained public wrappers or explicit friend
assemblies; no package-local generated binding remains public.

Smoke and release workflows run `verify-packed-protocol.sh` against the produced
NuGet package. Its isolated external consumer compiles current generated author
types from the restored package, verifies those restored DLL bytes came from the
input `.nupkg`, and rejects the removed legacy file-search method and types.

Release and Windows CI run the complete unfiltered source-mode behavioral
solution before the focused package-mode compatibility lane. Package identity
counts describe only that focused lane. Artifact-backed archive and native lanes
remain separate and fail instead of skipping whenever their lane is selected.
Test evidence freezes platform-neutral fully qualified and parameterized display
identities plus the complete skip-capable declaration policy; each observed skip
must match its exact project, owner, fully qualified and display identities, and
TRX reason.

The Stack contributor is a separate Core contract at semantic version `2.0.0`.
Agent runtime packages are Worker-only authoring surfaces; optional App projects
are role-isolated build inputs and do not create a second Runtime API.

## Release Evidence

`agent-release-evidence.json` records the hash-bound public artifact inspection
used for B1-B4 decisions. In particular, the removed process-based file-search
facet was absent from every inspected public Agent Contracts artifact. Released
workspace-path migration and Working Summary surfaces remain supported.

The explicit Registry source-adoption operation remains supported for a current
unmanaged package. Agent does not infer provenance, scan historical roots, or
adopt pre-V1 package records.

## Persistence Policies

- Agent and semantic-memory SQLite stores are durable and bound to their exact
  migration-ledger checksums and normalized live schema fingerprints.
- History search is a derived projection and may be explicitly rebuilt after
  confirmed schema corruption.
- Skills index/import-journal, MCP server records, and Codex session secrets
  write named versioned envelopes while retaining the documented released
  predecessor reader. Malformed or future documents are preserved and rejected
  according to the policy recorded in the candidate.

## Stable Boundary

This ledger is the Agent component of the immutable local Sunder 1.0
compatibility freeze. It does not authorize tagging or publication. The
reviewed central Protocol baseline is in `PublicAPI.Shipped.txt`;
`PublicAPI.Unshipped.txt` contains only its nullable header because the six
synchronous workspace API removals and six async replacements are finalized in
the pre-release 2.0 shipped baseline.
Release preparation must still resolve the exact immutable Core `sdk/v*` tag
and package provenance; smoke tracking of a mutable Core branch is not release
evidence.

The coordinated Core package policy includes authored managed PDBs and excludes
host/framework PDBs and XML documentation from runtime archives. No Agent future
delta is currently approved. Finer-grained generated-type accessibility is not
listed as a delta because the whole-binding public/internal split already hides
every package-local transport type without removing central extension-authoring
types. Any later concrete reduction requires a new explicit compatibility
decision and invalidates this freeze.

## B1-B5 Decisions

- B1: retain workspace-path migration because Agent 1.0.5-1.0.7 released it and
  durable workspace paths require it.
- B2: remove legacy process-based file search as an approved clean break; it was
  never released and structured Local/Docker search is authoritative.
- B3: retain Working Summary because every released Agent Contracts artifact
  carried it and durable projections use it.
- B4: retain released durable readers, normalize released `-fast` model ids,
  reset only the obsolete global selected-session key, preserve/diagnose Docker
  remediation state, and reject ambiguous aliases.
- B5: retain only explicit current unmanaged-package Registry source adoption;
  reject inferred provenance and historical-layout scanning.
