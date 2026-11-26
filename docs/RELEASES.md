# Agent package releases

**The unit of release is one package.** Each of the 15 projects in
`packages.json` owns its literal `<Version>`. A protected `main` push starts
[Release](../.github/workflows/sunder-package-release.yml). It compares each
project against published releases in its own major. An unchanged version
doesn't build, create a tag or publish. Stable versions use `latest`; prereleases
use `preview`. Releasing a maintained major must not move either tag backwards.

### Test → release → publish

The main Agent (`younics.ai.agent`) goes first if selected. Each extension then
runs [the same package workflow](../.github/workflows/sunder-package-ship.yml)
independently. Its plan contains the extension and the exact published Agent
version in its contract lock. The owner plan contains only Agent. A failing
extension leaves the owner and passing siblings alone. A new owner failure blocks
an extension that needs that new version; an extension pinned to an already
published owner can still ship.

1. **Test.** Run the package's `SunderTestOwner` suites on the six native targets.
   Keep discovery, TRX and required-platform case checks with their job. Retain
   the qualified role bytes from that build after the tests pass. Compose them
   once into a `.sunderpkg`, inspect it, and exercise that exact archive through
   the shipped App and its bundled Host on all six RIDs.
2. **Stage the release.** Verify the six receipts, locked dependencies and
   installed-size budgets. Create this package's `<inventory-key>/v<version>` tag
   and GitHub draft only after qualification. The draft retains the tested
   archive, original execution reports, source, checksums, SBOM and recovery plan.
3. **Publish to Hub.** Upload this exact version initially **Yanked**. Each
   native runner downloads it anonymously from Hub, installs it under real
   Registry policy and exercises its lifecycle. Retain all six installed
   receipts, list just this version and advance its channel tag in one
   revision-fenced operation, then announce the GitHub release. The dependency
   is verified read-only. The Hub writer is queued because policy revisions are
   shared; a failed writer does not cancel the other packages.

MCP tests its projected native library; OpenAI tests its auth flow; Memory
tests SQLite; Shell tests process fault isolation; Agent tests its App snapshot
and RPC stream. Maker uses its own template toolset and also creates, builds
and activates a package with Agent **absent**. Those belong to their respective
packages. Full-family integration, authoring and stress remain in
[Validate](../.github/workflows/sunder-package-smoke.yml) on PRs, merge queues
and scheduled/manual integration runs.

### Ordinary CI inputs

Build with the SDK in `global.json`, locked public NuGet dependencies, and the
Node/npm versions in `.node-version` and `.npm-version`. No sibling Core
checkout is required. `eng/ci/app-release.json` selects the shipped DMG,
AppImage or Windows full archive. NuGet owns SDK integrity; Agent owns the
package locks, payload checks, native behavior and release receipts. The
active install-size budgets in `eng/ci/native-release.json` together fit the
existing family ceiling.

For one package locally, after its selected dependencies are published:

```sh
package='["younics.ai.agent.provider.openai"]'
work=$(mktemp -d)
python3 -B scripts/ci/agent_ci.py resolve --test-dependencies --packages "$package" --output "$work/inputs"
python3 -B scripts/ci/agent_ci.py validate --packages "$package" --inputs "$work/inputs"
```

For the initial unpublished owner, **Validate** builds six native leaves and
checks its composed bytes against the separately authenticated lock-authoring
archive before consumer builds. Release likewise reproduces the lock-selected
owner before publication. The authoring archive is a reference for comparison.
Do not substitute it for a build, rewrite a lock during CI, or credit an
emulated run as native qualification. See [native inputs](native-release-inputs.md)
and [cold acceptance](cold-release-inputs.md).

### Initial `2.0.0` cutover

Only for the original all-new 15-package cutover and unchanged reviewed
Windows Local sources and locks, record the 34 original Windows Local cases per
ABI as `not-selected`. Two exact source fingerprints qualify: the reviewed
cutover and its two-file Maker managed-updater fix. The second changes none of
the 733 authored inputs compiled by Windows Local on either ABI. The package
plan carries that original selection across independent releases. No old
Windows reports are loaded or counted. Ordinary
validation and later releases run the live cases. **`2.0.1` remains on hold**
until installed `2.0.0` ↔ `2.0.1` works in both directions on all six RIDs.

### Recovery and configuration

The read-only planner uses GitHub's draft-read permission (`contents: write`).
The protected `sunder-agent-release-promotion` environment controls staging
and visibility. Configure `SUNDER_REGISTRY_API_URL` for the intended HTTPS Hub
and its `SUNDER_REGISTRY_API_KEY` with `package:publish` and `package:release`
on the exact IDs. Hub checks the publishing identity and grants when the
workflow uploads and lists each version. If Hub rejects a package, use its
response to resolve that package's authorization or version conflict.
Both calls to the reusable shipping workflow must explicitly pass that named
secret. The staging job checks its presence and format before creating a tag.

For an interrupted stage, rerun the original failed job with its original
tested artifact. Once its draft is complete, recover that package's retained
source, bytes and receipts; never rebuild a tagged package, overwrite an
immutable asset or issue a different visibility request on an ambiguous
reply. A conflicting tag, draft or Registry identity requires reconciliation
for that package alone. A historical batch draft must use its original
workflow at its original source. The [reverse workflow](../.github/workflows/sunder-agent-cohort-rollback.yml)
prepares a reviewed reverse request and does not apply it.

If only the release controller changed after a **complete single-package draft**
was staged, the next protected-main Release can resume it. Selection authenticates
the original main push, its successful six-RID qualification and staging step,
and compares the draft bytes to that job's original qualified artifact. Writers
pin the original asset IDs and hashes, verify the tag, and check out the original
producer separately for verification and installed testing. Product, lock,
test-harness or release-policy changes outside the reviewed controller files
block this path. Missing evidence stops recovery.
