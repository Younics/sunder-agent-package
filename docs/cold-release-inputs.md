# Reviewed cold release inputs

Agent source, native producer and fan-in jobs consume the same independently
approved durable candidate/catalog bytes. This is an orchestration input manifest,
not a replacement for Core qualification, catalog authority or cache receipts.

## External CI input contract

Before the Agent tag, configure the existing protected
`sunder-agent-release-promotion` environment with:

- The existing exact `SUNDER_AGENT_ARTIFACT_SOURCE_COMMIT`,
  `SUNDER_AGENT_LOCK_SET_SHA256`, `SUNDER_AGENT_RELEASE_APPROVAL_REFERENCE`, and
  `SUNDER_AGENT_RELEASE_OWNER_PRINCIPAL_ID`.
- **`SUNDER_AGENT_COLD_INPUT_MANIFEST`**, a compact JSON asset locator with exactly
  `repository`, positive numeric `releaseId` and `assetId`, `name`, positive `size`,
  and lowercase `sha256`. Its digest/size are independently approved values, not
  values copied from the download response. The manifest limit is 256 KiB.

For example (replace every illustrative identity with approved values):

```json
{"repository":"Younics/sunder-agent-package","releaseId":123,"assetId":456,"name":"cold-inputs.json","size":1234,"sha256":"<64 lowercase hex characters>"}
```

The asset contains the closed
[cold-inputs schema](../scripts/release/cold-inputs.schema.json):

```json
{
  "schemaVersion": 1,
  "artifactSourceCommit": "<approved tagged Agent source commit>",
  "lockSetSha256": "<exact aggregate production lock-set SHA-256>",
  "core": {
    "artifactSourceCommit": "<approved Core SDK source commit>",
    "sdkVersion": "1.1.0"
  },
  "candidates": [
    {
      "packageId": "sunder.package.agent",
      "version": "2.0.0",
      "packageContentIdentity": "<inspected candidate content identity>",
      "publicInventorySha256": "<complete inspected public-inventory hash>",
      "asset": {
        "repository": "Younics/sunder-agent-package",
        "releaseId": 123,
        "assetId": 457,
        "name": "Sunder.Package.Agent.2.0.0.sunderpkg",
        "size": 1234,
        "sha256": "<archive byte SHA-256>"
      }
    }
  ],
  "platformCatalog": {
    "authorityId": "sunder.core",
    "releaseVersion": "1.1.0",
    "catalogSha256": "<canonical catalog content SHA-256>",
    "asset": {
      "repository": "Younics/sunder-core",
      "releaseId": 234,
      "assetId": 567,
      "name": "sunder-platform-contracts.v1.json",
      "size": 1234,
      "sha256": "<same canonical catalog SHA-256>"
    }
  }
}
```

Candidate entries are sorted by package ID/version/content/inventory identity and
must cover exactly the committed selections, including complete public inventory
and descriptor/ABI/authority records. The initial family profile accepts one exact
candidate per package ID and one Core catalog covering every direct and
inventory-only platform constraint. Missing, extra, duplicate, changed or
conflicting inputs fail before restore. Candidate archives are bounded to 256 MiB;
the catalog to 64 MiB. Names preserve the approved asset's actual casing; package
identity comes from inspection rather than a generated-project filename guess.

`fetch` constructs GitHub API routes from numeric identities, checks the selected
release/asset ID, name, size, uploaded state, GitHub digest and exact numeric URL,
then bounds and hashes the downloaded bytes. It writes to a fresh external input
directory. There is no latest-tag resolution, branch download, local-directory
search or online substitute for a missing approved input. The protected prepare
job passes the exact reviewed locator to all source/native/fan-in jobs; each job
downloads and independently verifies the same bytes.

The Core SDK/catalog workflow owner must supply the retained numeric catalog asset
identity and matching Core source commit. The coordinator supplies the reviewed
base-seed candidate and the final lock-bearing Agent source identity. The Agent
workflow neither creates those publications nor rewrites their selections.

## Cold preparation and offline execution

Python **3.9+** is explicitly checked in all four release/control workflows and in
the producer. Provision `python3` on every runner (including Windows ARM64), plus
the pinned .NET SDK, Git, GitHub CLI, Bash and native PowerShell 7 where required.
An absent/old executable is a prerequisite failure, not a compatibility fallback.

`release_inputs.py prepare` performs this sequence:

1. Reverify the source commit, clean production inputs, approved manifest and all
   candidate/catalog bytes. Snapshot every production contract lock, owned baseline,
   release dependency lock and `packages.json`.
2. Allocate a **new external Sunder home** and explicit
   `SunderContractCacheRoot=<home>/contracts/v1/sha256`. Core's C# cache does not
   implicitly follow `SUNDER_HOME`; both the property and Node cache environment
   are supplied. An existing home is rejected rather than adopted or cleaned.
3. Explicitly restore tool/dependency inputs with `Configuration=Release`, the
   exact `SunderAgentVersion`, `ContinuousIntegrationBuild=true` and the selected
   source/package mode. Package mode is locked. Restore never passes `-r`; verify
   all six declared RIDs in the evaluated project and restored assets graph.
   Source CI explicitly builds and retains Core tooling before network denial when
   no retained immutable toolset was supplied.
4. In the denied-network process tree, invoke Core's `RestoreSunderContracts` on a
   combined package/platform consumer **with only the exact catalog**. It must fail
   with `contract-cache.entry-missing`, leave the cache absent and preserve source
   bytes. This proves that the job did not inherit a warm developer cache.
5. Call the same Core target with each project's exact selected candidates and
   required catalog. No Registry origin override, add/update/select/bind, Accept or
   Freeze operation is used. Pinned authoring origins retain their canonical `/`;
   origin-free locks remain origin-free. Core alone publishes cache evidence.
6. Recheck source and input bytes and retain local `cold-state.json` for execution.
   `exec` revalidates it, denies network, supplies identical evaluated properties
   and cache roots, disables shared build servers, executes the requested build or
   fan-in, and verifies source/lock/baseline invariance even after failure.

The native producer uses this shared preparation and wraps every native Build and
prebuilt Pack in `exec`. Fan-in uses the identical preparation **before** entering
the frozen no-Restore assembler. Its nested MSBuild processes inherit the same
Release/version/CI/cache/compiler settings. Source/package behavioral tests and
native runtime lifecycle tests retain their normal networking needs; it is package
hydration and artifact building that run in the denied-network boundary.

## Network-denial boundary

The built-in launchers use Linux user/network namespaces (`unshare`) and macOS
`sandbox-exec` with `deny network*`. A failed launcher stops before hydration/build;
there is no proxy-only or unsandboxed retry. A child Python process must fail to
reach a live loopback listener outside the boundary before commands are accepted.
No external network endpoint is used by that probe.

Windows, or a runner where the built-in facility is unavailable, must provide a
reviewed process-tree network-denial adapter. Configure repository/organization
variables for each applicable suffix `WIN_X64`, `WIN_ARM64`, `LINUX_X64`,
`LINUX_ARM64`, `OSX_X64`, `OSX_ARM64`:

- `SUNDER_AGENT_OFFLINE_RUNNER_<SUFFIX>`: absolute path of a pre-provisioned
  executable adapter;
- `SUNDER_AGENT_OFFLINE_RUNNER_SHA256_<SUFFIX>`: its independently reviewed hash.

The self-contained adapter interface is `adapter -- executable arg...`; it must execute that exact
child and all descendants with network denied, propagate its exit code, and leave
source/cache files intact. The code verifies executable bytes, takes a read-only
hash-matching private snapshot, then runs the same live-loopback child probe.
The adapter is trusted reviewed runner infrastructure;
the probe is a check, not a substitute for the process-tree isolation review.
Credentials used for input download/publication are removed from build environments.

## Local qualification

```sh
python3 -B scripts/test/release/release-inputs.fixture.py
python3 -B scripts/test/release/native-family.fixture.py
bash scripts/test/release/native-release.fixture.sh
bash scripts/test/release/promote-agent-family.fixture.sh
```

The cold fixture mocks service/tool processes and verifies byte/identity tampering,
full RID/property consistency, the cold diagnostic, exact hydration, lock mutation,
source tool retention and fail-closed offline execution. The promotion fixture now
creates fresh schema-3 capture through the native owner's implementation and runs
its real final verifier; it no longer relabels schema-2 marker evidence.

Read-only real-archive comparison is separately available with
`scripts/test/release/real-release-metadata.fixture.py <agent-root> <existing-core-cli.dll>`. It inspects
all fifteen retained Debug archives/committed selections and compares three owning
Core Format API inspections (base, App-bearing provider, headless tools). It does
not rebuild or publish any package and does not qualify native production runners.

`scripts/test/release/real-cold-restore.fixture.py <agent-root> <existing-Sunder.Package.Build.dll>
<retained-core-catalog.json>` separately executes the real Core restore task against
isolated copies of all fifteen committed locks. It proves the missing-cache
diagnostic, exact offline hydration and unchanged replay bytes/mtimes under the
actual network-denial launcher, without building or modifying a Core/Agent project.
