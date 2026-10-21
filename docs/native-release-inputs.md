# Qualified native release inputs

The Agent native scripts consume Core's **qualified generated-role inputs V1**.
Native release provenance is **schema 3**. Schema-2 marker-only native evidence is
rejected; it cannot be upgraded by relabeling or rehashing an old handoff.

Core's authoritative contract is
`sunder-core/docs/package-development/PREBUILT-ROLE-INPUTS.md`, with schemas in
`sunder-core/specs/package/v1/prebuilt-role-inputs/`. Core validates the complete
canonical evidence, frozen owned APIs, import lock, evaluated source/configuration,
restored dependency graph, toolset, role plan, receipts and native leaf index.
The Agent transport validates bounded membership and byte identity before extraction;
it does not replace that qualification or generate receipts.

## Producer and native test handoff

Requirements: PowerShell 7, Python 3.9+, Bash, and the pinned .NET SDK. Review and
freeze **all** owned baselines before the first native Build. The producer verifies
their frozen/published state; it does not refresh locks or freeze contracts. Keep
source, locks and tools unchanged during the complete native operation.

From the Agent repository root:

```powershell
./scripts/release/build-native-family.ps1 -Rid osx-arm64 -Version 2.0.0 `
  -OutputDirectory artifacts/native-build/osx-arm64 `
  -ColdInputsDirectory /retained/agent-inputs -ColdManifestSha256 <approved-sha256> `
  -ArtifactSourceCommit <approved-agent-commit> -CoreCommit <approved-core-commit>
```

The producer explicitly restores the locked graph, builds each canonical project
for the exact native RID, then uses a compact Core V1 selection and prebuilt Pack
to produce the 15 `current-rid-archives` used by native lifecycle/metric tests.
It does not call `GetSunderPackageRoleInputs` after Build: that target depends on
Build. NuGet mode is the default (`SunderUseSourceSdk=false`). `NUGET_SOURCE`, when
set, selects the explicit producer restore source.

The producer now invokes the shared [reviewed cold-input preparation](cold-release-inputs.md)
before its first Build. It allocates a new isolated Sunder home/cache, proves Core's
pre-hydration missing diagnostic, restores exact approved candidates/catalog without
lock changes, and routes Build and prebuilt Pack through the verified network-denied
executor. The manifest/durable input identities and source/Core commits are required
explicit inputs; a warm developer cache cannot replace them.

Ordinary Core Build exports, beside each canonical RID leaf:

```text
src/<project>/bin/Release/net10.0/sunder-targets/<rid>/
  role-inputs.v1.json
  role-inputs.v1.json.sha256
  sunder-dev/                    # complete Worker and optional App inputs
```

Apply native signing using Core's **pre-index Worker signing hooks**, before this
qualification is exported. Subsequent signing, reindexing or editing invalidates
the qualified bytes. Native tests must consume the producer's retained archives.

After the existing native lifecycle/metric gate, call
`python3 -B scripts/release/native-family.py record` with its ten positional arguments. It:

1. Checks the whole qualified leaf against Core's exact file list and digest sidecar.
2. Matches every non-composed native file to the tested archive, allowing Core's
   package-shared/role-shared/RID storage projection without changing any bytes.
3. Checks exact Core metric identity, counts and thresholds.
4. Writes `native-leaves-<rid>.tar.gz`, `native-leaf-evidence-<rid>.json` and metric copies.

Each package in native schema 3 retains `workerProject`, `leaf`, `testedArchive`,
`metricsSha256` and `metrics`. It adds:

```json
{
  "qualification": {
    "path": "younics.ai.agent/osx-arm64/role-inputs.v1.json",
    "sha256": "<exact reviewed Core evidence digest>",
    "size": 12345
  },
  "metricsSize": 1234,
  "sourceRoleUses": {
    "lockSha256": "<exact qualified contract-lock digest>",
    "roleUses": []
  }
}
```

`leaf.path` is now `<packageId>/<rid>/sunder-dev`; `leaf.hasApp` binds the qualified
role set. Obsolete `markerPath` and `markerSha256` are removed. `leaf.files` still uses `{path, sha256, size}`; Core's
unchanged evidence uses `{path, bytes, sha256}`. Tar membership is exactly the
qualification, its LF-terminated digest sidecar, every qualified leaf file, and
`metrics/<packageId>.<rid>.json`. Paths are independent of the producer checkout.
The digest is bound into the owning CI provenance; an unauthenticated sidecar or
locally fabricated provenance alone is not release approval or native attestation.

`sourceRoleUses` is copied from the qualified Core Worker receipt and rechecked
against that receipt during transport. It is carried through fan-in so the release
reader can verify separately authored typed Runtime uses against the current lock
and all six RID qualifications. Package API `consumerRoles`/`requiredContracts`
still govern their complete import groups. Provider imports grant no consumer use
by themselves: an additional use must match the source receipt's exact contract,
descriptor/ABI, role, required flag and actions. In particular, Web's optional
execution-target discovery does not grant invocation, subscription or App use.
Older schema-3 captures without this projection are usable only when their qualified
receipts contain no separately authored role uses.

## Fan-in prerequisites and execution

Before invoking the assembler, the owning workflow must explicitly restore every
canonical project with the same SDK, dependency locks and source mode used by the
producers, including all declared RID graphs. For the normal NuGet release mode:

```sh
dotnet restore <canonical-project.csproj> --locked-mode --source "$NUGET_SOURCE" \
  -p:Configuration=Release -p:SunderUseSourceSdk=false -p:RestoreLockedMode=true \
  -p:SunderAgentVersion=2.0.0 -p:ContinuousIntegrationBuild=true
```

Then call `python3 -B scripts/release/native-family.py assemble` with its eight positional arguments.
The assembler performs **no Restore, Build, Compile, source generation, package
execution or Host launch**. It validates all six uploaded bundles before extracting
any of them, extracts into a fresh private directory beneath the artifact output,
and emits a compact Core selection per package. Every Pack call uses:

```text
-t:PackSunderPackage
-p:Configuration=Release
-p:BuildProjectReferences=false
-p:SunderPackageUsePrebuiltLeaves=true
-p:SunderPackageComposeRuntimeIdentifiers=win-x64%3Bwin-arm64%3Blinux-x64%3Blinux-arm64%3Bosx-x64%3Bosx-arm64
-p:SunderPackagePrebuiltRoleInputsFile=<private-selection.json>
```

All six RID selections are explicit; evidence paths, property order, compact UTF-8,
no BOM and no terminal newline follow Core V1. There is no current-machine fallback.
Core rejection ends the operation, without an ordinary-Pack or build retry.

### Explicit immutable tools for workspace callers

For source-workspace operations, set these variables consistently on producers and
fan-in (and explicitly restore in that same source mode before fan-in):

```sh
export SUNDER_NATIVE_USE_SOURCE_SDK=true
export SUNDER_NATIVE_TASK_ASSEMBLY=/retained/core-tasks/Sunder.Package.Build.dll
```

Build and retain the **complete exact task set separately**, before entering these
operations. The helper checks the eight managed task/dependency assemblies and
passes `SunderPackageBuildTaskAssembly` plus
`SunderPackageBuildTaskSourceIsImmutable=true`. Core checks their actual contents.
Do not point this at mutable outputs being rebuilt concurrently. Installed NuGet
toolsets are selected by Core normally. Fan-in never bootstraps source tooling.

### Transport and publication behavior

- Exact six-RID/15-package inventory; no extra native input artifacts or selection
  entries. Optional standalone metric copies must be complete and byte-identical.
- Portable ASCII paths, depth/length limits, no rooted/parent/device/backslash paths,
  case collisions, duplicate entries, file/directory collisions, links/reparse
  points, sparse files or unapproved tar metadata.
- Full tar preflight precedes extraction. Only regular files and a bounded POSIX
  long-path extension are supported; extraction never uses `tar -x`/`extractall`.
- Core's 64 MiB qualification, 16,384 files, depth-32 discovery, 65,536 discovered
  entries and 2 GiB per-leaf bounds apply. Tar metadata is bounded before allocation;
  provenance JSON has independent size/depth/structural limits.
- Handoffs/output belong under `artifacts/` or outside reviewed source. Producer
  paths are never extraction destinations. No native `obj` or generated tree is needed.
- Final verification compares **all** retained non-composed files, including App
  bytes, through the final archive's shared/RID projection. Manifest and content
  index composition remain Core-owned.
- Pack/verification failure preserves the prior family. Publication is serialized
  by `.native-family-publication.lock`, replaces archives individually, and writes
  `native-test-evidence.json` last. A process interruption during publication can
  leave an incomplete family: verification fails closed and the same handoff can
  be rerun. If the publication lock survives a terminated process, remove that
  directory only after confirming the prior publisher has stopped. Never publish
  a directory until `native-family.py verify` succeeds.

## Release-owner integration

The owned workflow/publish/promote/acceptance/handoff changes are separate:

1. Provision Python 3.9+ on producer and fan-in runners, alongside existing tools.
2. Use the same reviewed/frozen source, exact restored SDK/tool inputs and source
   mode on all native jobs and fan-in. Add the explicit fan-in restore step **before**
   `native-family.py assemble`; restore is never hidden inside the assembler.
3. Keep existing native artifact names (`sunder-agent-native-leaves-<rid>`) and
   `native-leaf-evidence-<rid>.json` / `native-leaves-<rid>.tar.gz` upload/download
   names. Merge all six artifacts, including their metric directories.
4. The promotion fixture now creates fresh qualification, metric, leaf and bundle
   bytes through `native-promotion.fixture.py`, captures them with the real native
   implementation and invokes the actual schema-3 final verifier. It includes
   qualification digests, sizes, portable paths and `leaf.hasApp`; marker-only
   schema-2 data is not relabeled or accepted.
5. Continue the existing native lifecycle, metric, output-audit, publication and
   provenance checks; qualifying inputs does not waive those gates.

## Local checks and qualification scope

```sh
python3 -B scripts/test/release/native-family.fixture.py
bash scripts/test/release/native-release.fixture.sh
```

The Python fixture exercises actual capture/transport/verification and a mocked
MSBuild process: relocation after deleting producer outputs, preserved Worker/App
and shared payloads, bounded long paths, unsafe tar inputs, tamper/missing/extra/
mixed/stale inputs, immutable-tool selection, Core rejection without fallback,
failure preservation and resume. It does not execute real Workers, compile a family,
qualify six native operating-system runners, attest signatures or publish anything.
