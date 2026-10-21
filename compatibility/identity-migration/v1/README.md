# Agent runtime identity migration — map v1 and predecessor capture

This is the **pre-edit handoff**, owned by the Agent repository. The user approved
runtime package identities and **all 19 public RPC identities**, plus corresponding
private identities and display names. `map.json` records that decision; it is not
an applied migration or release acceptance record.

The later reader/inverse implementation and pending owning-tool steps are recorded
in [the post-cutover historical-reader handoff](history-adaptation.md).

## Version and identity decisions

- `schemaVersion: 1` describes this Agent-owned document shape.
- `identityMapVersion: 1` is the first reviewed identity mapping. It is independent
  of package, contract, SDK, lock and migration-oracle schema versions. Reference
  this version **and the map file hash** in later inverse/artifact evidence. A
  changed identity decision needs a new versioned map and predecessor linkage;
  do not repurpose this capture or update historical oracles to match new source.
- All 15 package versions stay **2.0.0**. The main identity is `younics.ai.agent`;
  suffixes carry across, except Builder becomes `younics.sunder.package.maker`.
- All 19 `sunder.agent.*` public IDs become `younics.ai.agent.*`. Each new public
  contract family starts at **1.0.0**, even when its old family currently has
  version 2.0.0, 2.1.0, 3.0.0 or 4.0.0. This does **not** reset or rewrite old
  identities, histories, package releases, descriptor bytes or ABI hashes.
- All 13 private successor mappings are explicit in the map. Private versions
  follow package version **2.0.0**. The owner-runtime and semantic-inspector IDs
  keep their suffixes under `younics.ai.agent`; other explicit IDs use their
  owner's replacement package prefix, including Maker's `.runtime`.
- The main automatic private ID becomes `younics.ai.agent.agent.profile`; its
  automatic provider becomes `younics.ai.agent.agent.profile.provider`. Keep this
  automatic declaration automatic. Explicit provider IDs, service IDs, method
  IDs and service keys keep their existing values.
- Main display name: **AI Agent**. Builder: **Sunder Package Maker**. All other
  exact names and all 15 unchanged project paths are in `packages` in the map.
- C# project/folder/assembly/authored namespace names stay as captured. A project
  may need a runtime identity property edit, such as `SunderPackageFamilyId`,
  without renaming the project or changing its assembly identity.
- `sunder.core` / `sunder.stack.contributor` / **2.0.0** is an external platform
  authority and is explicitly preserved.

Do not insert `identityMapVersion` into SDK owned baselines, locks, package
inventories or successor schemas which do not define that field. This map and
its Agent-owned proof documents carry it. The schema intentionally has no fields
for future artifact, descriptor, ABI or content hashes.

## Permanent evidence and exact before-images

All paths here are relative to this directory unless stated otherwise.

| Path | Meaning |
| --- | --- |
| `map.json` | Complete 15-package / 19-public / 13-private / one-auto-provider mapping |
| `map.schema.json` | Closed JSON Schema; also checked by the dependency-free local checker |
| `predecessor/manifest.json` | SHA-256 and byte lengths of all 33 permanent copies; index and capture stability proof |
| `predecessor/manifest.sha256` | Exact digest of the predecessor manifest, additionally pinned in `map.json` |
| `predecessor/files/src/<unchanged project>/sunder-contracts.owned.json` | All 15 original **current working-tree** owned baselines, including prior dirty SDK-hardening changes |
| `predecessor/files/src/<unchanged project>/sunder-contracts.lock.json` | All 15 original locks, copied without parsing/reserializing them |
| `predecessor/files/packages.json` | Original package/provider/capability inventory |
| `predecessor/files/compatibility/agent-service-public-successors.json` | Original current public successors |
| `predecessor/files/compatibility/agent-ordinary-private-successors.json` | Original current private successors |
| `predecessor/contract-history-index.json` | Derived pointer/index into captured JSON; never a replacement for its byte-exact originals |
| `predecessor/source-before-manifest.json` | Exact current-file hashes, index entry identities, file modes, exclusions and recorded absences |
| `handoff.json` | Map/schema/checker hashes and actual final pre-edit verification results |

The predecessor manifest SHA-256 is:

```text
7dec732776212922f3c1534ffe8fae9119f57b6d0876efc631c9eba1d3afb904
```

Capture counts:

- **33 permanent byte-exact copies**: 15 owned + 15 locks + inventory + two current
  successor documents.
- **19 current public tuples + 35 public history tuples**: all 54 retain their
  old IDs, versions and actual payload hashes.
- **12 retired-private current tuples**, no retired-private history entries in
  these owned documents. The separate current private successor set has **13**
  entries, including the automatic main profile; these are different sets.
- **1,788 working files copied**, **26 PreCanonical files hashed in place**, and
  **21 absent paths recorded** (including already-staged deletions).

The source selection is the union of the Git index and HEAD paths, plus current
nonignored untracked files. It includes the untracked
`AgentSourceMigrationProjectChanges.ApprovedImportedServiceNames.cs` and prior
cleanup files. Capture-owned files are excluded from this predecessor selection.
Generated output directories (`bin`, `obj`, `artifacts`, `generated`, `dist`,
`node_modules`, `TestResults`, `sunder-dev`, `__pycache__`) and generated binary
outputs are excluded. `PreCanonicalProjects` is hash-only: it stays in its
original frozen location. Authored fixtures with `Generated` in their filenames
are still captured; a substring is not treated as build output.

Full source/project/test/review before-images and original Git index bytes are at
the approved temporary parent, under:

```text
agent-identity-migration-v1-before/
  files/<repository-relative path>
  source-before-manifest.json
  git/index.bin
  git/index-entries.z
  git/head.txt
  git/head-paths.z
  git/status.z
```

On the capture machine the parent is
`/var/folders/fx/6_dmjj8j40b50b8wcr7n_vx40000gn/T/opencode`.
These temporary copies are review material pending a compact, checked source
inverse. The permanent source manifest binds their bytes even after that inverse
has been prepared. It is not authorization to restore the entire tree or index.

The **raw Git index** SHA-256 before and after capture is identical:

```text
eab9249ebdb7c38ff91f7d4eca5c5ad40f35b34fed82242b50236e39551d068b
```

Each copied or hash-only input was read again after capture. A changed input,
index, HEAD, unrelated Git status or selected file set prevents a success
manifest. `handoff.json` additionally records the final live-tree verification.
This records the dirty working tree, not `git show HEAD:path`, and preserves
both staged and unstaged prior work.

Original `agent-compatibility-candidate.json`, `agent-rpc-migration-baseline.json`,
`agent-release-evidence.json`, source-history fixtures and PreCanonical oracles
remain in place. Do not rewrite them, their old tuple identities, or the copied
retired-private evidence. The captured JSON bytes are authoritative, including
projection reviews and any lock fields whose claims have not been requalified.
New owned baselines, current-successor records and selected locks must later come
from the owning review/build tools and genuine new artifacts. Copying historical
locks here neither validates their archives nor supplies new artifact evidence.

## Generated namespaces and module names

The map specifies the exact old/new package namespace stems for all 15 packages.
Render each as:

```text
Sunder.Generated.Packages.<stem>_Services
Sunder.Generated.Packages.<stem>_Models
Sunder.Generated.Packages.<stem>_Internal
```

For example, `Sunder_Package_Agent` becomes `Younics_Ai_Agent`; Builder's stem
becomes `Younics_Sunder_Package_Maker`. Capitalization is the generator's first
character per ID segment rule: `Lmstudio` and `Openai`, not the authored project
spellings `LMStudio` and `OpenAI`.

Public contract-scoped generated symbols are listed individually, for example
`CSunder_Agent_Execution_Target` → `CYounics_Ai_Agent_Execution_Target`.
The same rule applies to the mapped private contract IDs where used by transport
and generated reference types. Owned interfaces remain directly in their
package's Services namespace; imported interfaces use the contract-symbol
subnamespace. Transport types use the Internal namespace plus contract symbol.
Account for generated service-reference names containing those symbols as well.

The role composition module namespace is **identity-independent**:

- `Sunder.Generated.Package` → `Sunder.Generated.Package`
- `Sunder.Generated.Package.AppModule` stays the App module type.
- `Sunder.Generated.Package.WorkerEntryPoint` stays the Worker entry type.

These rules were read from Core's `GeneratedPackageNames.cs` and
`SunderPackageCompositionEmitter.cs`. Generated namespace references in authored
source are expected identity-derived edits; the surrounding authored namespaces
and assembly names remain unchanged. Generated output files themselves are not
reviewed source inputs and were not captured.

## Compose the later source inverse with existing approvals

The immediate predecessor is **the captured dirty source**, which already
includes the approved contract-scoped imported-service naming changes. It is not
the earlier fixture text or HEAD. Preserve the existing 35 naming before/after
pin pairs. The handoff checker proves that all 35 captured source files match
their existing `AfterSha256` pins using the actual source-oracle normalization.

Introduce a reviewed outer inverse, conceptually:

```csharp
current = BeforeApprovedIdentityMigration(relative, current);
current = BeforeApprovedImportedServiceNames(relative, current);
current = BeforeApprovedUnnamedProviderContent(relative, current);
current = BeforeApprovedDomainBoundary(relative, current);
// Existing response-retirement/source-split and earlier inverses follow.
```

In particular:

1. Put the identity inverse **before** `BeforeApprovedImportedServiceNames` at
   the current-source entry to `BeforeApprovedSourceSplits`.
2. `ReadApprovedDomainBoundary` also invokes the imported-name inverse directly
   on current source. Its current-file read needs the identity inverse first.
3. Audit other direct current-source hash paths, including
   `ReadApprovedSourceSplits`, `IsApprovedSourceSplitAddition`, canonical/support/
   retained-project checks and owned-public-service checks. Apply the outer
   inverse exactly once on each path that consumes an identity-edited file,
   before the original hash or historical reconstruction. Do not bypass a pin
   because a path has a second reader.
4. For every changed source/project/test file, review the diff against its raw
   temporary before-image, pin the genuine post-edit whole-file hash, reverse
   only the approved exact edits with occurrence-count checks, then assert the
   reconstructed predecessor hash. Missing/extra changes must fail. Added files
   need explicit reviewed addition records; absent predecessors are not invented.
5. The existing C# oracle hashes strict UTF-8 decoded text with CRLF/CR normalized
   to LF. Derive compact inverse pins with **that exact rule**, independently of
   raw-byte evidence. The capture's supplementary `normalizedTextSha256` strips
   a UTF-8 BOM as documented in its manifest; do not use that field as a blanket
   substitute for the C# rule. The checker uses the C# rule for all 35 naming pins.
6. Commit a compact, hash-bound inverse/review record linked to map version/hash
   and the permanent source manifest. Do not permanently duplicate all 1,788
   temporary source files or change existing frozen before-images/pins to make
   new source fit.

The identity inverse must restore the **contract-scoped** `CSunder_Agent_*`
aliases and old generated package stems before the existing naming inverse runs.
That earlier inverse then restores its own predecessor, including Web's reviewed
explicit-use-target change. Collapsing both steps into an old flat namespace
would skip the already-approved SDK-hardening boundary.

## Commands and tool boundaries

Run from the Agent repository root; these commands need only Python and Git:

```sh
# Read-only permanent map/schema/history/frozen-evidence checks:
python3 -B compatibility/identity-migration/v1/check.py check

# Small map/path/hash/no-overwrite guards; no product builds/tests:
python3 -B compatibility/identity-migration/v1/check.py guards

# Read-only comparison with the still-pre-edit working tree and temporary copies:
python3 -B scripts/compatibility/capture_identity_migration.py verify-before
```

The one-shot `capture` command has already run. A second capture is rejected;
there is no overwrite/force/cleanup/restore mode and no arbitrary input/output
path option. It writes only fixed new evidence/temp destinations and exclusively
creates copied files. Missing inputs, unexpected counts, path escapes, symlinks,
unsupported index modes and changed input hashes fail closed. Existing descriptor
hashes and domain-separated ABI hashes are checked without rewriting JSON.

`python3 -B compatibility/identity-migration/v1/check.py handoff` is also one-shot:
it checks the map, negative guards, live before-images and prior naming pins, then
exclusively creates `handoff.json`. After identity editing starts, use `check` for
permanent evidence; `verify-before` should then fail on intentional source changes.

This preparation runs no product builds/tests, publication, live Registry calls,
state migration, staging or commits. Later migration acceptance is separate.
