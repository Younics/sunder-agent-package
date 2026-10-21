# Physical pre-canonical residue cleanup

`scripts/dev/agent_residue.py` inventories the reviewed Agent-only retirement
residue. It defaults to **dry-run**. Explicit apply requires a matching dry-run
report and repeats the complete eligibility check, then rechecks each root before
removing it. Run it while this checkout is idle. Python 3.9+ and POSIX no-follow,
descriptor-relative filesystem APIs are required; unsupported platforms fail
closed. No .NET or Node tools are invoked.

## Exact removal scope

The hardcoded allowlist is independent of the report supplied to `--review`:

- Whole artifact-only roots: `fixtures/PackedProtocolConsumer`,
  `src/Sunder.Package.Agent.Contracts`, main Agent `OwnerProtocol`, Builder,
  Execution.Docker, Execution.Local, Memory.Semantic, Provider.Anthropic,
  Provider.Gemini, Provider.LMStudio, Skills and Subagents `Protocol`, Mcp and
  Provider.OpenAI `Shared`, and **Skills `App`**. Files must be inside each root's
  immediate `bin` or `obj`; other descendants may only be empty directories.
- Only the immediate `App/bin` and `App/obj` of the other **11** source-bearing
  App directories: Agent, Builder, Execution.Docker, Execution.Local, Mcp,
  Memory.Semantic, Provider.Anthropic, Provider.Gemini, Provider.LMStudio,
  Provider.OpenAI and Subagents. Source-bearing App roots and other directories
  are retained, with the exact empty-only consolidation exceptions below.
- Only `bin/Debug/net10.0/osx-arm64` directly under the **15** canonical family
  roots in `packages.json`. Every file must predate **2026-10-05 00:00 UTC**, the
  exact-RID dependency file must reference retired protocol assemblies, and newer
  sibling `sunder-targets/osx-arm64/role-inputs.v1.json` must have the correct
  project/configuration/TFM/RID and matching SHA-256 sidecar. Current sibling
  `sunder-roles`, `sunder-targets`, and `sunder-dev` must exist. This classifies the
  old direct outputs; it is not a replacement for native release qualification.
- **Preserved current RID intermediates:** an empty canonical direct-RID directory
  is ordinary active output layout. A recent nonempty direct-RID output is also
  preserved when its dependency metadata has no retired protocol dependency and
  the current sibling qualification matches the package identity in `packages.json`.
  Qualification project paths may use either the exact repository-relative or
  `src`-relative canonical path. These preserved cases do not fail `--check` and
  are never empty-pruning candidates. Recent legacy or unclassified nonempty
  direct-RID outputs still fail closed.
- **Reviewed source-consolidation empties:** `App/Properties` in Agent,
  Execution.Docker, Execution.Local, Mcp, Memory.Semantic and Provider.OpenAI;
  the entirely empty App trees of Agent, Mcp, Provider.OpenAI and Subagents;
  and `Agent/App/Runtime` only as part of that empty Agent App tree. These are
  eleven exact directories, not a general App traversal rule. The six removed
  `App/Properties/AssemblyInfo.cs` paths and Subagents' removed
  `App/RuntimeNamespaceMarker.cs` may remain in the Git index as **missing**
  working-tree files. Only these seven missing-path exceptions are allowed;
  their staged content and current deletion state are preserved. Canonical
  assembly attributes must remain tracked and present for the six merged cases.
  Any actual file or unreviewed directory prevents pruning that tree; links,
  special files and unreviewed tracked paths cause refusal. Removal uses only
  `rmdir`, never file deletion.
- Empty directories under `src`, outside output, retained App and frozen/history
  trees, plus empty `fixtures`, `.circleci`, and `scripts/__pycache__`. The dry-run
  projects which parents become empty after removing the allowlisted roots.

The 15 canonical project paths and the parent directories of the frozen
26-project retirement inventory must still match. That historical inventory is
read for corroboration, never edited or used to expand removal scope.

## Guards and evidence

Every candidate must contain only ordinary directories and Git-ignored regular
files. Tracked/index files, restored HEAD paths with staged deletions, nonignored
untracked files, active projects, links/reparse points, special files, hard links,
filesystem boundaries and known qualified-output/archive/evidence names cause
refusal. A blocked candidate prevents the entire apply preflight. Empty pruning
never bypasses a blocked root. Descriptor-relative `unlink`/`rmdir` does not follow
links or recursively delete newly appearing contents.

Current canonical `bin`/`obj`, support-project outputs, role and target trees,
package archives, `artifacts`, compatibility evidence, all source/tests, and frozen
`PreCanonicalProjects` captures are retained. This tool does not change project
identity, PackageMetadata, contracts, source C#, Git state or build hooks.
The cleanup project's validation anchor is the current canonical inventory; frozen
retirement records are preserved bytes, not execution prerequisites.

With `--report`, the JSON includes:

- Exact before inventory, file/directory identities, byte totals, refusal reasons,
  and direct-RID classification evidence.
- SHA-256 manifests of tracked/HEAD/nonignored-untracked working files (including
  missing paths), the raw Git index and any split index, staged-entry and Git status
  digests, and a separate frozen-capture manifest.
- SHA-256 manifests of all retained files under `src`, `artifacts` and
  `compatibility`, including ignored current outputs. Directory timestamps are
  omitted from byte-preservation comparison because adjacent removal changes them.
- On apply, exact removed paths/counts, an after inventory and identical
  before/after preservation manifests. Partial progress is recorded on failure.

Reports must be new files outside the repository. Use a private temporary
directory: these inventories contain local paths and file hashes. A stale report,
changed source/index/output, regenerated candidate or modified policy requires a
new dry-run review. Reports never grant authority to delete additional paths.
After source consolidation or identity migration, capture a **fresh baseline of
the current tree**; do not compare pre-migration source bytes as though the migration
had not happened. Existing report files cannot be overwritten. Identity receipts,
`compatibility/identity-migration` and its retained predecessor artifacts are included
in the source/retained preservation manifests, alongside original archive names.

## Commands

From the Agent repository root, with an existing external evidence directory:

```sh
python3 -B scripts/dev/agent_residue.py
python3 -B scripts/dev/agent_residue.py --dry-run --report "$EVIDENCE/agent-residue-before.json"
# Review the inventory and classification before the explicit apply:
python3 -B scripts/dev/agent_residue.py --apply \
  --review "$EVIDENCE/agent-residue-before.json" \
  --report "$EVIDENCE/agent-residue-after.json"
python3 -B scripts/dev/agent_residue.py --check
```

### Read-only regeneration scan hook

After an otherwise-required build or migration check, callers can explicitly run:

```sh
python3 -B scripts/dev/agent_residue.py --check
```

Exit **0** means no allowlisted residue or prunable empty directories; **1** means
eligible residue exists; **2** means refusal, unexpected content or another error.
The default dry-run exits 0 for an eligible inventory, even when it is nonempty.
Verified current direct-RID intermediates are reported as **preserved**, not residue.
Newer legacy or unclassified nonempty direct-RID outputs are reported as refused,
not silently reclassified as old. Active canonical `obj` roots and original retained
archives are outside the removal scan regardless of the package's current identity.
This command is a read-only hook; no automatic build/CI cleanup is installed.

### Guard checks

```sh
python3 -B -m unittest discover -s scripts/test -p test_agent_residue.py -v
```

The lightweight tests use isolated temporary Git indexes and filesystems. They
exercise traversal/allowlist rejection, tracked and untracked protection,
symlink/ancestor/reparse/special/hard-link refusal, active-project and evidence
protection, retired versus retained Apps, fixed-date direct-RID classification,
current-role byte preservation, exact consolidation-empty pruning with missing
tracked files, current-RID/active-obj/original-archive retention, stale/tampered review and
concurrent-change rejection, dirty staged/unstaged preservation, and regeneration
detection. They perform no .NET/npm build, artifact cleanup, publication or commit.
