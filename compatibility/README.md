# Agent Compatibility Freeze

`agent-compatibility-candidate.json` is the generated Agent 2.0 compatibility
ledger. It is derived from source declarations, descriptor bytes, package
metadata, release locks, public API files, live SQLite schemas, document schema
policies, configuration/environment/telemetry vocabulary, and the checked-in
public release evidence.

`agent-rpc-migration-baseline.json` is the implementation-first migration
oracle. It freezes canonical descriptor and ABI bytes, service/method/error
inventories, bindings, providers, uses, audiences, bundles, and the exhaustive
source disposition for the central Contracts boundary plus all twelve local
Protocol/Shared boundaries. It is evidence only, never a package build input or
publication authorization.

Regenerate it only after reviewing the intended compatibility delta:

```sh
./scripts/compatibility/update-agent-compatibility-candidate.sh --write
./scripts/compatibility/update-agent-compatibility-candidate.sh --check
```

Normal test runs regenerate the same bytes in memory and fail when the checked-in
candidate differs. The generator also asserts the architectural inventory:

- 31 Agent RPC descriptors: 19 public bundled descriptors and 12 package-local
  owner descriptors;
- descriptor schema version 1 and current Agent contract semantic version
  `1.0.0`;
- 18 supported third-party extension points plus the reserved first-party
  `sunder.agent.tool.execution.observed` bridge;
- 63 provider declarations and 46 consumer declarations;
- exactly 15 Worker V1 runtime package families. The obsolete Protocol project is
  physically removed and is not a sixteenth Agent runtime or release artifact.

The unchanged migration baseline records the original 19 central public bindings
and 12 package-local bindings. Phase 5 relocated genuine business source to its
owner and physically removed the exact 26 obsolete projects and authorized support/
wrapper files. Historical source is authenticated data under
[`PreCanonicalProjects`](../tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects/RETIREMENT.md),
never compiled replacement code. Current history guards/inverses and the final full
required Main/source run pass (**1,835/0/6 scoped skips**, including actual all-family
artifact acceptance), and all 26 project retirements are recorded. Final source-SDK
artifacts, parallel workspace Build/Rebuild and three isolated local NuGet-mode
representatives also pass. These local results do not constitute remote Core release,
Registry publication, native or performance acceptance.

The obsolete `verify-packed-protocol.sh` and `PackedProtocolConsumer` fixture are
physically removed with their authenticated captures retained. Smoke/release
workflows target fifteen canonical archives and no Protocol NuGet. Keep the actual
`check-protocol-retirement.mjs` feed/residue gate. Physical cleanup and passing
historical oracles do not imply final integration or publication qualification.

Release and Windows CI run the complete unfiltered source-mode behavioral
solution before the focused package-mode compatibility lane. Package identity
counts describe only that focused lane. Artifact-backed archive and native lanes
remain separate and fail instead of skipping whenever their lane is selected.
Test evidence freezes platform-neutral fully qualified and parameterized display
identities plus the complete skip-capable declaration policy; each observed skip
must match its exact project, owner, fully qualified and display identities, and
TRX reason.

The Stack contributor is a separate Core contract at semantic version `2.0.0`.
Agent packages use one authored project with generated, isolated Worker and optional
App roles. Local archives and a local Core catalog support unpublished-family API
selection without Registry access; local selections omit `registryOrigin` and do
not supply publication evidence.

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

These ledgers are pre-cutover migration evidence. They do not authorize tagging,
publication, or retention of a public Protocol package. Existing public API
snapshots remain inputs only until the feed audit and implementation-first parity
gate determine their historical disposition.
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
