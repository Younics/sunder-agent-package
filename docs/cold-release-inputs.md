# Cold acceptance inputs

Ordinary CI and native release jobs resolve dependencies automatically and use
`agent_ci.py` preparation/execution. They do not require a hand-maintained cold
manifest or offline-runner variables. See [releases](RELEASES.md#ordinary-ci-inputs)
and [native inputs](native-release-inputs.md).

`release_inputs.py` separately owns explicit cold-cache and denied-network acceptance.
It uses the same complete package/catalog lock validator and Core restore target;
it does not write contract selections, freeze APIs or invent cache receipts.

## Automatically generated inputs

```sh
python3 -B scripts/release/release_inputs.py fetch \
  "$PWD" /tmp/agent-cold-inputs "$(git rev-parse HEAD)" "$(git -C ../sunder-core rev-parse HEAD)"
```

Both source identities above come from the actual checkouts, not configuration.
`dependency_inputs.py` locates exact published dependencies. Unpublished owners
are prepared in two phases: `agent_ci.py resolve --allow-missing` retains the
catalog and available archives in `preparation.json`; six native component jobs
produce one composed candidate, then `agent_ci.py complete-inputs` admits that
archive against the unchanged production locks. A single-host seed is insufficient.
Identical coordinator asset copies are valid, conflicting copies are not. The
output directory must be new and outside the source trees.

The generated [schema-3 input inventory](../scripts/release/cold-inputs.schema.json)
contains source/lock hashes, Core `buildToolVersion`, exact package content/public
inventory identities, sorted `packageIds` and the selected Core catalog. The scope
includes the evaluated dependencies of selected test roots; each consumer may use
a verified subset. Local file records contain
`name`, `size` and `sha256`. Those are computed evidence, not operator approvals.
The schema-2 and numeric-locator schema-1 readers remain for retained cold fixtures;
it is not normal CI/release configuration.

Inputs must cover every committed direct and inventory-only contract selection.
There is one exact candidate per package ID and one Core catalog. Candidate archives
are bounded to 256 MiB, catalog bytes to 64 MiB. Actual package identity comes from
archive inspection, not filenames. Missing, extra, conflicting or changed inputs
fail before hydration. Published downloads verify numeric release/asset identity,
uploaded state, name, length and SHA-256; only completed releases are discovered.

## Explicit cold preparation and execution

Runners need Python 3.9+, the pinned .NET SDK, Git, `gh` and Bash. Native producers
also need PowerShell 7. `release_inputs.py prepare`:

1. Checks the actual clean source/Core identities, generated manifest and complete
   dependency bytes; snapshots authored project versions, locks and owned baselines.
2. Requires a new external Sunder home/cache, with both `SUNDER_HOME` and
   `SunderContractCacheRoot` supplied explicitly. It never adopts or clears a warm home.
3. Restores the full declared RID graph in Release/CI with the selected source/package
   mode. Package mode is locked. Source tooling is retained before network denial.
4. Denies network to the process tree and probes a combined consumer with only the
   catalog. Core must report `contract-cache.entry-missing` and preserve source/cache.
5. Hydrates exact selected candidates/catalog through `RestoreSunderContracts`,
   without add/update/select/bind, Accept/Freeze or origin overrides.
6. Records `cold-state.json`. `exec` rechecks inputs before/after each command and
   uses the same evaluated properties, cache and denied-network boundary.

Workspace `workspace_ci.py prepare`, `build` and full E2E consume this explicit
profile. Artifact builds are denied network; real Host/Docker lifecycle tests retain
the networking they actually need. Ordinary `prepare-ci`/`build-ci` is separate.
Neither profile changes the no-rebuild qualified-role fan-in boundary.

## Network denial

Built-in launchers use Linux user/network namespaces (`unshare`) or macOS
`sandbox-exec` with `deny network*`. A child must fail to reach a live loopback listener
outside the boundary. There is no unsandboxed or proxy-only fallback.

Where those facilities are unavailable, explicitly selected cold acceptance can
use `SUNDER_AGENT_OFFLINE_RUNNER` and `SUNDER_AGENT_OFFLINE_RUNNER_SHA256` for a
pre-provisioned, reviewed process-tree adapter. Its interface is
`adapter -- executable arg...`; it must preserve arguments, exit status and source
files. The helper checks its bytes, snapshots them read-only and probes the child.
This is runner infrastructure for an explicit isolation test, not a prerequisite
for publishing a new package version. Download/publication credentials are stripped
from build environments.

## Local checks and evidence scope

```sh
python3 -B scripts/test/release/test_dependency_inputs.py
python3 -B scripts/test/release/release-inputs.fixture.py
python3 -B scripts/test/release/native-family.fixture.py
bash scripts/test/release/native-release.fixture.sh
python3 -B scripts/test/release/test_agent_pipeline.py
```

Mocked fixtures cover discovery, byte/identity tampering, unchanged locks, RID/property
consistency, cache diagnostics, tool retention and fail-closed isolation. They do not
qualify real native runners or publication.

`real-release-metadata.fixture.py <agent-root> <existing-core-cli.dll>` separately
inspects retained archives and Core Format API results without rebuilding/publishing.
`real-cold-restore.fixture.py <agent-root> <existing-Sunder.Package.Build.dll>
<retained-core-catalog.json>` exercises the real restore task on isolated lock copies,
including missing-cache diagnostics, offline hydration and unchanged replay bytes.
