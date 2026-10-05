# Historical project captures

These are test evidence, never build inputs. Keep their bytes immutable. The
original oracle remains `compatibility/agent-rpc-migration-baseline.json` (SHA-256
`a9f4563d6095b57d66ca915821218ef596a0845ab17839d3e5bba5e394a899eb`).

## Provenance

- The fourteen `<Family>.csproj.txt` files are exact copies of the LF-normalized,
  complete pre-edit Read-tool captures preserved by the canonical migration pass
  on 2026-10-04. Their capture hashes and line counts were checked against that
  pass's `before-images/` and `summary.json`. They include earlier staged and
  unstaged migration additions; they are not the original frozen projects.
- `Agent.index.csproj.txt` is the authentic 86-line Git index blob
  `ad5b86b2bd84e7da7e59c3c09e1eb38f2ec35dd0` obtained with the read-only operation
  `git show :src/Sunder.Package.Agent/Sunder.Package.Agent.csproj`. It is an earlier
  migration stage, **not** the uncaptured final 87-line pre-replacement project.
  It still excludes `AgentWorkerProviderCatalog.cs`, before that exclusion became
  `ProviderComposition.cs` plus `AppComposition.cs`, and predates the worker-only
  SQLite asset tag. Its explicit inverse reconstructs the frozen hash exactly.
  The HEAD project was also investigated and rejected: its normalized hash is
  `8dee782760920930c10a63469335075810649a4ae86caa4759f954c27ae2d5bd`, not the frozen hash.

`AgentSourceMigrationProjectChanges.CanonicalProjects.cs` pins both capture and
reviewed current-project hashes. The existing inverse additions reconstruct the
original projects, including their dependency, reference, Compile, resource and
output declarations. The main Agent uses the explicit index-stage inverse above.

| Capture | Lines | Original frozen SHA-256 after inverse |
| --- | ---: | --- |
| Agent.index | 86 | `c0bea6c831ea57ebca793f0a0bc118a99b48584fb4bfe59b74da4bd90d4cebae` |
| Builder | 41 | `048d986c0ed4b0c2fefb7d371990fd6452899d5c4ec772ee60c4cb2538416de6` |
| Execution.Docker | 50 | `34971a5725c36ab61c6da2b9eb30cc0979a11a858be73c15ab84d7dbd7ffcaff` |
| Execution.Local | 50 | `e7464217c7e12831edd90c6b404711ffc2e2c167033fec6584abf05dfac09809` |
| Mcp | 68 | `928957d516518c96c728bc06f224564649f09007d35977e2c2c6dd992fd4e392` |
| Memory.Semantic | 58 | `858d02fdaa9091fb6fc2c681636d689840f32ac6a75812a38102630aaa453b64` |
| Provider.Anthropic | 59 | `a6015b0c6c22f1db24326d098f9647b24beef79152082e91968168f6b229e72f` |
| Provider.Gemini | 60 | `29d9d4008f2e77d322b6bf070d31b0e8e094572115718ad3c5bab7366ee17366` |
| Provider.LMStudio | 60 | `6ac0b7ee23a35f754bee37b12235affcdfd61088907a667b5c30aa6a821c96a0` |
| Provider.OpenAI | 60 | `20ad50414fa2c592832aa0849f382ba2f82cea82d7aca5e139438217741128d7` |
| Skills | 51 | `5187311c12fb848c52bcb314b866da262230aca2ede822b4107acd487fa04e3f` |
| Subagents | 57 | `fda30846620f0b5a0a29acab6a40d34fa2398b8e34c5b5161004d13f07549f23` |
| Tools.Files | 27 | `4cfcd27ce6b5dd5bbfcf4e7acd33e69434779efb1f4695a524ab0c21a91ea829` |
| Tools.Shell | 26 | `2d3d90f6b77a07f5fe5665ec559d35ce25c47255f48dfdad8ea3041a89cea78f` |
| Tools.Web | 27 | `1124878987631cfa7bb189a3b295b39f1c00110b90d95bfaf73f75e05ed60e79` |

## Acceptance boundaries

Returning a capture is conditional on checking the complete current canonical
project, its imported source/resource lists and repository build defaults, six-RID
framework-dependent role settings, and dependency versions. `packages.json` must
still enumerate exactly these fifteen canonical roots. Explicit project mutation
cases prove that a fixture cannot conceal drift in current inputs.

The fast owned-contract closure check ties the fifteen canonical roots' current
`sunder-contracts.owned.json` files to all 31 original descriptors (19 public),
including exact descriptor/ABI bytes, hashes, versions, exposure and unique owner.
The later 38 central interface moves are explicit and hash-checked separately from
the original 142 business/catalog paths; only five exact shared-role annotations
are inverted for those newly relocated sources.

The full frozen baseline test still compares the complete original inventory
(31 descriptors, 19 public contracts, 57 projects, 252 Compile links, 96 adapters).
Helper retirement and test-project migration require their own reviewed history;
that evidence now lives in `retirement-history.json`, `helper-support-history.json`
and `retirement-dispositions.json`. These fifteen captures do not exempt other
projects or source files from checks. See [RETIREMENT.md](RETIREMENT.md) for the
exact retirement inventory, original hashes, provenance and validation boundaries.
`deletion-inventory.json` is the exact machine-readable file list, including the
separately captured out-of-solution PackedProtocolConsumer and its obsolete verifier.
That fixture is not part of the immutable 57-project RPC baseline.

Actual generated Worker/App/archive tuple, policy and dependency closure remains
the required `AgentSourceArtifactMigrationTests` lane. Its canonical build helper
builds the authored roots and checks unchanged selected/owned evidence. Pure
historical reconciliation does not establish that generated outputs are fresh.

## Pre-canonical compatibility candidate

`compatibility/agent-compatibility-candidate.json` is also immutable pre-canonical
history, with SHA-256 `0c01e196c11a344981e44b486d1f6b065ed677344611cb9b5da7cee35429959e`.
Its former update environment switch is rejected; it cannot rewrite either history
oracle. The owning test reconstructs descriptors, declarations, extension points,
project roles, package metadata, API snapshots and vocabulary through authenticated
history, while still capturing the live SQLite schemas and JSON-document policies.

`candidate-inputs-history.json` preserves 43 authentic Git index inputs: the old
package inventory, shared Worker defaults, and all 41 runtime release-lock files.
Every lock hash and the package-inventory hash match the pre-canonical candidate.
The fixture SHA-256 is `8d8c034e0c0d2650b7a1ef043ca057678e948f3ae371e3fda12e922a6f584cfc`.
These snapshots are data only and do not constrain current canonical NuGet restores.

Current compatibility is checked separately against all fifteen already-built
canonical manifests, their owned descriptors and descriptor payloads, exact provider
and use policies, role targets, authority/exposure, dependency/import closure and
capabilities. The Agent worker receipt also uses verified historical declarations
instead of requiring the removed manual targets group. No artifact builder runs in
these focused history/candidate checks.
