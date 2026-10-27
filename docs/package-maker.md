# Sunder Package Maker

Sunder Package Maker (`younics.sunder.package.maker`) creates, opens, builds, and
loads **local package projects**. Choose .NET with an Avalonia UI, or Node.js with
a React/TypeScript UI. Each starter includes the matching package worker.

> **Source channel:** This guide describes the current source. Creation uses the
> coordinated Sunder template and npm creator version associated with the installed
> SDK. Source qualification uses freshly packed matching Core artifacts; a standalone
> release requires those coordinated artifacts and its reviewed NuGet lock pins.

Projects live in folders you choose on the computer running Sunder. Package Maker
works without Agent. Its optional Agent tools accept only physically contained
projects in the selected local workspace; they never reinterpret container or
remote execution paths as local paths. Opening a project inspects its source
without installing an SDK, restoring dependencies, or building it.

## 1. Projects

Open **Sunder Package Maker** to see your saved project bookmarks. Each row shows
the source path, technology, and current state.

- **Create project** opens the creation wizard.
- **Open folder…** finds supported package projects in a local folder. One match
  opens directly; several matches show an entry-point chooser.
- **Manage SDKs** opens Package Maker's settings.

Bookmarks are deduplicated by canonical source entry point. Distinct folders can
have the same package ID, although Sunder may refuse to load conflicting
development sources at the same time. A missing folder stays in the list: use
**Locate folder…** to reconnect it or **Remove from projects** to forget it.
Removing a bookmark does not delete its source files.

On a wide panel the project list and details are side by side. On a compact panel,
select a row to open details and use **Projects** to return to the list.

## 2. Create a package

The wizard has three steps:

1. **Technology:** choose .NET/Avalonia or Node.js/React/TypeScript.
2. **SDK setup:** Package Maker prepares the latest supported stable SDK for the
   selected technology. Progress and cancellation are available here and in
   Sunder's background activity. A failed setup can be retried.
3. **Project:** enter a package name and ID, choose a parent folder, and edit the
   subfolder name. The full-path preview is the destination that will be used.

The package ID and subfolder follow the name until you edit each field yourself.
The parent folder defaults from your recent saved projects, or your home folder.
Opening the wizard does not save a draft bookmark or create source files.

SDK download and installation progress updates one current status line. Command
output is available under **Details**; a failed command shows one concise error
summary while retaining its diagnostic output there.

Creation scaffolds the project, pins its SDK, restores dependencies, and performs
the first build. Success opens project details with **Load into Sunder** as the
next action; creation does not automatically load the package.

If creation fails before producing source, your entered values remain available.
If source already exists when an operation fails or is cancelled, Package Maker
remembers it as **Setup incomplete**. Use **Retry setup** to restore and build
that source instead of trying to create over it.

## 3. Project details

### Load, rebuild, and unload

**Load into Sunder** sets up and builds the project, then asks the Host to validate
and load its development output. The Host owns runtime prerequisites, development
overlays, and package activation. Details show runtime readiness and App readiness
separately; a running worker does not by itself mean that the App UI activated.
An active development overlay can temporarily override an installed package.

Package Maker does not automatically watch, build, or reload source changes.
Status, source-metadata, catalog, and log observations remain read-only.

**Build** produces and validates the current-platform development output. It never
loads or reloads a package, including one already loaded. **Retry setup**, archive
creation, and name edits likewise do not replace a loaded activation. Builds reuse
unchanged restore and compiler inputs where supported.

**Hotreload Package** requires an existing Maker-owned development session. It
performs one finite build, stages that exact validated output revision, awaits the
required App retirement, and verifies the matching applied Runtime/App revision.
The UI and Agent integration use the same session for the canonical project entry
point, regardless of which surface acquired it. An Agent Hotreload can refresh a
package loaded manually in Maker; it does not create a second development owner.
Matching only a package ID is insufficient to select a different source project.
A failed build or rejected stage retains the last good activation. An uncertain
reload retains its successful build and original operation identity: **Resolve
Hotreload** observes or resolves that operation without rebuilding. Other project
mutations are blocked until it is resolved or unloaded. A successful build alone
is not evidence that the new revision has been applied.

The page and operation checks share activation-owned progress: loading, reloading,
waiting for the matching App revision, applied, or unconfirmed. Previous applied
output remains explicitly historical while a reload is pending. Reload's bounded
execution budget is separate from owner-release cleanup and from a tool's brief
wait. Only the originally admitted reload resolves lost stage/commit replies,
using its exact original identity; status checks never acquire a replacement owner.

An unconfirmed release keeps its exact ownership. Reconnect and retry **Unload**;
do not replace the handle or assume a successor Worker completed the old job.

**Unload** reconciles this project's finite jobs and releases its development
handle. It does not claim to stop another owner's session. Navigating
away or closing a view leaves activation-owned work running. Deactivating Package
Maker cancels its jobs, drains its monitors, and releases its handles.

### Edit source metadata

The name and package ID are read from source. Changes in the editor are drafts
until you choose **Save changes**. **Discard** reloads the latest observed values.
External changes are detected; conflicting drafts remain visible and must be
reviewed rather than silently overwriting newer source.

Automatic package-ID editing is deliberately bounded to recognizable private
starter shapes: the inferred private .NET `SampleService` starter or the private
Node.js `messages` service starter and its supported generated bindings. It is
not a general identity migration tool. Customized/computed service identities,
public contracts, retained owned baselines, publication/fork history, or unproven
generated evidence can make the ID read-only. Details show the inspector's exact
reason. Preserve that evidence and use the appropriate authoring/identity workflow
for more advanced projects; deleting history is not permission to rename an ID.

Before an ID change, Package Maker releases the exact old development owner. The
edit never implicitly loads the new identity; use **Load into Sunder** explicitly.
A name edit retains the loaded owner without reloading it. The source inspector,
rather than bookmark metadata, determines the resulting identity and edit eligibility.

### Other actions

- **Build** produces the current-platform development package without loading or reloading it.
- **Hotreload Package** builds once and applies the exact revision to an already loaded package.
- **Create package archive** checks the reviewed APIs and creates a `.sunderpkg`.
  It does not publish to a Registry. Publication is a separate workflow.
- **Package logs** opens Sunder's package-filtered developer logs, useful for
  runtime and App activation failures.
- **Build output** contains bounded output for this project. Switching projects
  does not move another project's output or status into the selected detail.
- **Open folder** opens the source directory in the system file manager.

Finite project jobs appear in the main background activity surface; SDK jobs
appear in settings activity. Queued and running work can be cancelled there.
Cancellation waits for worker cleanup when a job has started and recovers any
partial project result. Before accepting work, Builder selects an exact backend
incarnation. A lost Start response is reconciled by reading the same job on that
backend, not by resending the mutation. A transient App caller handoff shows
**Reconnecting** while retaining the accepted job and project ownership. Status
reads and idempotent stop reconciliation are bounded by the existing twelve-second
local budget, inside the backend's sixty-second orphan lease.

A replacement Builder Worker cannot accept, cancel or acknowledge the previous
backend's job. Permission, protocol and explicit backend-replacement failures are
not caller-reconnection signals. If shutdown is unconfirmed, retain the job's
exact identity and project exclusion, reconnect, then retry **Unload** before
starting replacement work. An unconfirmed stop is not a backend `failed` outcome;
the last working package remains loaded. Confirmed terminal results are retained
in a bounded cache so repeated stop acknowledgement does not contact another
backend or repeat disposal.

### Optional Agent tools

The Agent surface is one focused development toolset. Opening Maker's page is not
required. Manual Maker Build and combined build/hotreload remain unchanged.
Existing tool selections map through aliases; screenshot selection is new and
opt-in. Apply, view opening and screenshot permissions are explicit new actions:
an old reload/input approval does not silently approve those broader operations.

| Tool | Input and result |
| --- | --- |
| `maker_package` | `package`: absolute `.csproj` / `sunder.package.json` path or a returned project reference. `query`: `status` (default), `runtime`, `views`, or `outputs`. Returns only that selection. |
| `maker_apply` | `package`, optional built `output` reference. Validates already-built output, loads it initially or reloads through its existing Maker owner, and awaits that operation's outcome. Never builds/restores. Multiple unloaded output candidates require explicit selection. |
| `maker_logs` | `package` plus severity/source/time/literal `contains` filters, or `entry` alone to expand a returned diagnostic. |
| `maker_view` | `target` view/element reference; literal `role`, `name`, `text`, optional `within`, `limit`, and `cursor`. Ordinary editable `value` is opt-in; protected values are excluded. |
| `maker_screenshot` | `target` view/element; optional `detail` (`normal` / `high`). Returns an actual image, not base64 text in model context. |
| `maker_interact` | `target` and advertised `action`; `text` only for `set-text`. `open` explicitly opens a registered package view. Optional `verify` checks literal displayed text separately from dispatch. |

Build and test through Shell, apply the result, query the needed view/log portion,
interact, then verify. Paths work directly: an initial status call is not required.
Returned references contain an activation namespace; they cannot acquire a new
meaning after restart. Keep references opaque. The Host retains full source,
owner, revision, workspace and authorization evidence rather than repeating it in
each model response. Null optional arguments mean omitted; required arguments,
unknown fields, duplicate keys and invalid types still reject before authority.

`maker_apply` is explicitly permission-gated for **load and reload**. It reuses the
loaded output/owner and returns `unchanged` when that exact output is already
applied. Status is not a build freshness claim: the source inspection fingerprint
is not proof that edits were compiled. A pending or uncertain operation excludes
another project mutation. Observation can confirm its matching applied revision
without restarting the action. Original operation receipts are never evicted to
make capacity for new mutations. Neither a replacement App nor a replacement view
inherits an old operation or element's authority.

The six-tool surface has no separate model-facing operation polling tool.
`maker_apply` keeps its original invocation pending while the Worker observes the
accepted operation, using cancellable waits with 200–1,000 ms backoff. App admission
remains short; waiting does not occupy the App command consumer. A duplicate call
observes the same original operation rather than dispatching it again. An uncertain
or lost acknowledgement is retained as such, never converted into permission to
replay the mutation. Cancellation stops observation; it does not claim to undo an
already dispatched action.

Queries return bounded semantic matches, not DOM dumps. View queries default to
20 items, cap at 50 and page bounded results. Repeat the same filters with the
returned `nextCursor`; `partial` distinguishes incomplete traversal/backend
coverage from an authoritative empty result. Element references expire after
30 seconds and never follow replacements. `dispatched` does not mean the desired
external application effect happened; optional verification reports its own
observation. No arbitrary script, selector, desktop input or screen coordinate is
accepted.

Logs default to ten Warning-or-higher entries from the last fifteen minutes, both
Runtime and App, with an internal response budget. Times require explicit offsets;
`since` is inclusive and `until` exclusive. `after` uses an original operation as a
time anchor, not causal attribution. Filtering precedes latest selection; entries
are oldest-first. `next` retains the original query: continue with `package`,
`cursor` and optional `limit` only. Exact consecutive repeats collapse with counts.
Expand a returned entry for bounded diagnostic identity fields. `historyGap` and
`truncated` remain distinct. Logs require a successful load by the current Maker
activation, survive target fault/unload, and never reacquire its owner. Shell/build
output and unrelated packages are excluded.

Screenshots are explicit, bounded captures of the referenced package surface, not
the desktop/window. Normal capture has a 1,280-pixel longest edge; high has 2,048.
Images have a separate 2 MiB / 4-megapixel final budget. Native surfaces must be
captured too; unsupported backends/overlapping native geometry fail rather than
returning an incomplete bitmap. Native WebView capture currently targets macOS
WKWebView; Windows/Linux native adapters require separate implementation and
qualification. The views query reports current capture availability. Agent copies
verified image bytes into existing session/call-owned attachment storage, with a
64-image / 32 MiB per-session screenshot quota. Only the newest current-run tool
image is included in model context. Durable history retains earlier captures.

Package text, logs and pixels are untrusted data, never Agent instructions. The
semantic query/capture APIs require the managed App capability
`package-development-feedback.v1`; retained diagnostic queries additionally use
`package-development-log-queries.v1`. Legacy capabilities do not imply these
implementations. Native pixel and provider HTTP-fake qualification are separate
from managed/DOM tests; source availability is not release acceptance.

## 4. SDK settings

Open **Manage SDKs** from Projects, or open this package's settings. The stable
.NET and Node.js cards show detected/current versions, the recommended version,
update availability, installation origin, health, npm version where applicable,
and the last check time. Checks happen when navigating to settings or choosing
**Check for updates**, not merely when a view is constructed.

Package Maker currently supports stable **.NET 10.0 LTS SDKs** (10.0.100 or later)
and **Node.js 24 LTS** (24.18.1 or later). Recommendations are resolved from the
official Microsoft and Node.js release catalogs within that supported policy;
they do not mean the newest preview or unsupported major version. Downloads use
official distribution archives and their checksums.

.NET templates use a separate, short private CLI home under `~/.sunder/bt`, keyed
by the owning Builder workspace, template and SDK identity. This keeps the .NET
template engine's named mutex within its path-length limit even when Sunder's
runtime storage path is long. SDK installations remain in Builder's SDK storage.

- **Install / update supported SDK** prepares a supported installation.
- **Repair** and **Remove** apply to Builder-managed installations.
- External installations are detected and usable but remain read-only here.
- An installation in use by a build cannot be removed; stop that work
  first. The backend reports the refusal without deleting the installation.
- Offline or failed update checks report **Update status unknown**, preserving
  the installed-version information. Retry the check when connectivity returns.

Existing project requirements are respected, including .NET `global.json` SDK
and roll-forward settings and supported Node.js/npm version constraints and pins.
Conflicting or unsupported pins produce an explanation instead of being silently
rewritten. Installing a newer SDK does not automatically retarget existing
projects. New projects receive pins for the selected SDK.

### Build SDKs and execution runtimes are different

Package Maker manages **project build SDKs/toolchains** in its own storage. The
Host manages **execution runtimes** used to activate package workers, including
Package Maker's own .NET worker. A healthy build SDK does not establish runtime
or App readiness, and an installed execution runtime is not necessarily a build
SDK. Use Package Maker settings for project SDKs and the Host's runtime settings
for execution runtimes; build SDKs do not belong in the Host runtime store.

SDK operations use the same backend as the creation wizard. Closing settings does
not cancel an accepted job; reopen the settings or use background activity to
follow it. Errors remain available until resolved, and cancellation does not
discard an existing healthy installation.

### Optional local NuGet source packs

Until the coordinated .NET developer packages are published, an instance can use
source-packed packages by placing `local-nuget.json` under its role-local workspace
at `development-toolchains/project-tools/local-nuget.json`:

```json
{ "schemaVersion": 1, "feedPath": "/absolute/path/to/a/flat/feed" }
```

These are the only two accepted fields. The folder must contain one
`<PackageId>.<version>.nupkg` for each of `Sunder.Sdk`, `Sunder.Sdk.Avalonia`,
`Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, `Sunder.Package.Build`, and
`Sunder.Package.Templates`, all at the Builder SDK's coordinated version
(currently `1.1.0`). Only flat folder feeds are supported; nested feeds are not
searched. Paths and selected archives must be regular, unlinked local paths.
The pointer is limited to 16 KiB, the folder to 4,096 top-level entries, each
archive to 512 MiB, and the selected archives together to 1 GiB.
Prepare updated package sets in a new feed directory, then change `feedPath` after
all six archives are ready. This keeps jobs from observing a partially refreshed
development package set.

Each .NET create/setup/build/archive/watch job, and the wizard's `ensure-sdk`
preflight, reads the pointer once. Invalid or incomplete configured feeds fail
with a diagnostic before SDK selection or project mutation. SDK status, repair,
removal, metadata editing, and Node jobs remain available. The pointer can be
added, replaced, or removed between jobs without restarting the backend; retrying
the same accepted job ID keeps its original selection and result.

Builder checks package identities, SDK dependency constraints, and required .NET
10 template/build artifacts, then atomically retains the six verified archives
under `project-tools/local-nuget/<content-hash>/feed`. The hash includes all six
archives, the version, feed path, and source policy. A private `NuGet.Config` maps
`Sunder.*` exclusively to that retained feed and other dependencies to NuGet.org.
The selection uses its own `tools/nuget-packages`, HTTP, plugin, and scratch caches,
so changed same-version source packs receive a fresh cache and template home. Source feeds, existing
cache selections, user NuGet settings, and dependency locks are not rewritten by
the resolver. Normal project restore still enforces the project's lock settings.
Missing `local-nuget.json` restores the ordinary public package behavior for new
jobs. This override does not download or replace an SDK by itself.

### Optional local npm source packs

Unpublished Node tooling can be selected independently with
`development-toolchains/project-tools/local-npm.json`, using the same two-field
document:

```json
{ "schemaVersion": 1, "feedPath": "/absolute/path/to/a/flat/npm/feed" }
```

The folder must contain `sunder-sdk-1.1.0.tgz`, `sunder-package-tool-1.1.0.tgz`, and
`create-sunder-package-1.1.0.tgz` (substitute the coordinated SDK version when it
changes). Builder validates all three package identities, required artifacts and
archive paths, then retains their exact bytes under
`project-tools/local-npm/<content-hash>`. Linked paths, partial sets, malformed
archives and changed retained caches fail closed; existing caches are not repaired.

Node create/setup/build/archive/watch jobs and `ensure-sdk` read this pointer once
per accepted job. New projects use `file:` dependencies on the retained SDK and
package-tool archives, and the creator is installed from the retained archive.
Third-party dependencies still use the normal npm registry. Merely adding tarballs
to npm's cache does not select them for unpublished package-name requests.

The resolver does not rewrite existing projects, dependency locks, user `.npmrc`,
the NuGet selection or the installed Node SDK. Archive storage must outlive created
projects; do not place the selected feed or retained cache in a disposable checkout.
Missing `local-npm.json` keeps public npm defaults for new jobs. Prepare a complete
replacement feed before changing the pointer; in-flight jobs keep their selection.
