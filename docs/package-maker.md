# Sunder Package Maker

Sunder Package Maker (`younics.sunder.package.maker`) creates, opens, builds, and
loads **local package projects**. Choose .NET with an Avalonia UI, or Node.js with
a React/TypeScript UI. Each starter includes the matching package worker.

> **Source channel:** This guide describes the current source. Creation uses the
> coordinated Sunder template and npm creator version associated with the installed
> SDK. Source qualification uses freshly packed matching Core artifacts; a standalone
> release requires those coordinated artifacts and its reviewed NuGet lock pins.

Projects live in folders you choose on the computer running Sunder. Package Maker
does not use Agent workspaces, execution-target mappings, containers, or remote
shells. Opening a project inspects its source without installing an SDK, restoring
dependencies, or building it.

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

**Rebuild and reload on changes** is on by default. It watches authored source,
builds updated development output, and requests reloads in Sunder. Turning it off
stops this owner's source producer and reload-watch request while keeping the
package loaded. If another owner still requests output watching, the status says
so.

The first build prepares dependencies and generated role inputs. Later .NET builds
reuse unchanged restore and compiler inputs. A newly started Watch performs its
initial rebuild, including source edits made while the previous session retired.
Node Watch keeps its build contexts alive and rebuilds the affected Worker or web
inputs. Shared code, contracts, configuration,
or dependency changes can require both roles to rebuild. A failed build leaves the
last successful package loaded, and correcting the source triggers another attempt.

**Build** on an already-loaded project validates fresh development output, inspects
source again, and replaces this project's development session. Replacement waits
for the exact source-watch job to stop and the Host/App to finish releasing the
old session before acquiring the new output. This applies with Watch on or off,
even when the output bytes are unchanged: manual Build explicitly restarts the
target Worker and reloads its App. Other project sessions remain owned. Successful
**Retry setup** and metadata-triggered builds of loaded projects use the same rule.
Building an unloaded project does not implicitly load it.

Manual builds, archive creation, and source edits pause the producer to avoid
concurrent mutations. Archive creation retains the loaded session; it does not
return development output for manual reload. For an unchanged, retained source
identity, watching is restored afterward. After a failed or cancelled manual
build, Package Maker retains the old session and can wait for the next source edit
before restarting the producer. This preserves
the last good loaded generation and avoids repeatedly rebuilding unchanged broken
source. If restarting fails, its error remains visible. An unconfirmed stop must
be reconciled before another mutation or watcher can start.

If releasing the old session fails, its ownership remains retained: reconnect and
retry **Unload** before replacing it or restarting Watch. If release succeeds but
loading the new output fails or is cancelled, the row stays unloaded and can be
retried with **Load into Sunder**.

**Unload** stops and drains this Builder session's producer and releases its
development handle. It does not claim to stop another owner's session. Navigating
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

Before an ID change, Package Maker stops its watcher and releases its old
development session. A successful edit of a previously loaded project is rebuilt
and loaded again; a failed ID edit or subsequent build leaves it unloaded. A name
edit retains the old session until its rebuild succeeds, then replaces it even
with Watch off. The source inspector, rather than bookmark metadata, determines
the resulting identity and edit eligibility.

### Other actions

- **Build** produces the current-platform development package and reloads it if
  this project was already loaded.
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
- An installation in use by a build or watcher cannot be removed; stop that work
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
