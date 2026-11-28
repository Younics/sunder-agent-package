# Agent package releases

**The unit of release is one package.** Each of the 15 projects in
`packages.json` owns its literal `<Version>`. A protected `main` push starts
[Release](../.github/workflows/sunder-package-release.yml). It compares each
project against published releases in its own major. An unchanged version
doesn't build, create a tag or publish. Stable versions use `latest`; prereleases
use `preview`. Releasing a maintained major must not move either tag backwards.

### Review and release

**Select versions** posts the protected `main` commit, Hub origin and every
selected `packageId@version` in the run summary. A pending draft from an earlier
commit also shows its original archive source. Review that summary once in
`sunder-agent-release-approval`. An empty selection finishes without an approval.
The approval is bound to that run, source, destination and package selection.

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
3. **Upload exact versions.** Each Hub writer downloads and verifies its tested
   GitHub archive, then uploads it **Listed**, with `setLatest=false`. Users can
   select the exact version immediately. Dependencies are verified read-only.
   An immutable receipt's initial availability may differ from its Listed state today.
4. **Install from Hub.** After all uploads in the wave finish, [installed
   acceptance](../.github/workflows/sunder-package-accept.yml) runs for the
   successful uploads. Windows, Linux and macOS each install and exercise the
   package on x64 and ARM64 under real Hub policy. At most **three installs
   across the entire wave** run at once. The release makes no Hub writes until
   all install jobs finish, so its own writes cannot invalidate install previews.
5. **Promote and announce.** Only packages with a successful install job and
   its receipt on every RID reach promotion. Verify and retain those six receipts,
   use a revision-fenced dist-tag PUT to advance `latest` or `preview` (preserving
   newer channels), then announce the GitHub release. Writers remain serialized.
   A failed package stays unannounced; passing siblings can finish in the same run.

When selected, the owner completes all five steps before the extension wave
resolves its locked dependencies. Otherwise the extension wave starts directly.

MCP tests its projected native library; OpenAI tests its auth flow; Memory
tests SQLite; Shell tests process fault isolation; Agent tests its App snapshot,
RPC stream and uninstall with Agent Chat active. Maker uses its own template toolset and also creates, builds
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
Its public destination defaults to `https://hub.sunderapp.io`; a repository
variable `SUNDER_REGISTRY_API_URL` can select a different HTTPS Hub. Configure
these environments before the first nonempty release:

| Environment | Deployment branches and reviews | Hub configuration |
| --- | --- | --- |
| `sunder-agent-release-approval` | `main` only; required release reviewer | No Hub secret. The job records the plan after reading back GitHub's approval. |
| `sunder-agent-hub-writer` | Explicit branch `main` only; no reviewer or wait rule | Environment variable `SUNDER_REGISTRY_API_URL` and environment secret `SUNDER_REGISTRY_API_KEY`, scoped to `package:publish` and `package:release` on the exact IDs. |

Selection checks the required reviewer and writer branch policy before asking
for approval. Only upload and promotion jobs use the writer environment. Each
checks its destination against the reviewed plan. Provision the key directly
in that environment from the credential store; the caller, review record,
native runners and install receipts do not receive it. Keep the existing
`sunder-agent-release-promotion` protection for historical workflows and rollback.

**Re-run failed jobs** keeps successful upstream jobs and their immutable
artifact IDs. A full rerun of the same run can finish the reviewed plan or a
subset: it authenticates the original approval job and its plan receipt. A
different Hub, source or expanded selection needs a new run and review. Install
receipts are taken from the successful job execution for that package and RID;
a stale artifact cannot stand in for a failed rerun.

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
producer separately to verify its original qualification. Fresh installed tests
run from the protected controller, using `eng/ci/installed-app-release.json`;
their receipts identify both the acceptance commit and the App image actually
run. Product inputs, locks and the producer's App pin stay fenced. Changes to
shared native harness files require reviewed exact blob pairs before recovery.
Missing evidence stops recovery.

PR validation also runs the installed harness read-only against an immutable
Listed Agent reference. Its result is required for executable changes; only
the six release jobs can supply receipts for promotion of a staged archive.
