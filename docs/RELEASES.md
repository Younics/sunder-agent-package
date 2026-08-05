# Agent Family Releases

Agent releases are coordinated family releases. One `agent/vX.Y.Z` tag builds and publishes `Sunder.Package.Agent.Protocol` plus every `sunderpkg` entry in `packages.json` from one Agent commit, one Core commit, and one version.

## Core Coordination

For a new release, Agent CI resolves `Younics/sunder-core` `refs/heads/main` once at workflow start. The resolved full commit SHA is passed to every Core checkout and recorded in both the draft release notes and `release-provenance.json`. A recovery rerun reuses that recorded SHA instead of resolving a newer `main`, so the release remains reproducible and auditable.

Cross-repository changes require this order:

1. Land the compatible Core changes on Core `main`.
2. Publish the Core developer packages required by `Directory.Packages.props` and regenerate Agent release locks against those exact remote packages.
3. Commit the coordinated Agent changes.
4. Confirm Agent smoke CI reports and passes against the expected resolved Core SHA.
5. Set `packages.json` `releaseVersion`, then create and push `agent/v<releaseVersion>` from the Agent release commit.

Do not start coordinated Agent CI before the required Core changes reach `main`; the workflow intentionally consumes the latest Core `main` commit available when the run starts. Do not create an Agent release tag until the matching `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, and `Sunder.Package.Build` versions are available from NuGet.org. The release workflow verifies that prerequisite and builds the Protocol once against the locked remote SDK before rebuilding the family from the resolved Core source commit.

## Stable Boundary

Before a stable tag, finalize the Protocol API ledger by moving accepted API entries into `PublicAPI.Shipped.txt`. `PublicAPI.Unshipped.txt` may retain `#nullable enable` and comments, but no API entries. The release prepare job enforces this condition.

The Agent 2.x line uses `[2.0.0,3.0.0)` for `Sunder.Package.Agent.Protocol` and `>=2.0.0 <3.0.0` for runtime dependencies. `agent/v2.0.0-*` is therefore rejected because it precedes the stable lower bound. The Protocol package keeps its Sunder SDK dependency on the SDK's independent supported minor line.

## Publication Order

The release workflow:

1. Builds and tests the complete solution against the Core `main` commit resolved at workflow start.
2. Packs one Protocol NuGet package and all 15 runtime archives.
3. Validates exact runtime bytes with `Sunder.Package.Format` through the CLI.
4. Installs, activates, unloads, reloads, and reinstalls the exact family in a clean Runtime test host.
5. Transfers the Runtime-produced base Agent UI snapshot and activates it through the App package host.
6. Creates a draft GitHub release whose notes and evidence record the exact Agent and Core commits, then publishes Protocol to NuGet.org.
7. Publishes every Registry version with a package-scoped publish token and `setLatest=false`, then refetches version details. Verification derives the required projection inventory and manifest SHA-256 from the local canonical archive, requires exactly `shared` plus every unique manifest target, downloads all artifacts, and independently verifies each projection descriptor, canonical manifest, selected content index, payload hashes, archive inventory, and projection content identity.
8. Moves all runtime package `latest` tags for stable releases or `preview` tags for prereleases only after every immutable artifact verifies.
9. Publishes the draft GitHub release.

A failure can leave verified immutable NuGet or Registry versions without promoted Registry tags. Once NuGet publication may have started, the workflow preserves the draft release and its evidence rather than deleting or replacing it. Rerun the same tag to recover: the workflow reuses the recorded Core commit, requires the same release asset and evidence inventory, and byte-verifies existing immutable NuGet and Registry artifacts before continuing. A mismatched artifact or source record stops recovery without overwriting the preserved evidence.

## Repository Configuration

The release workflow requires:

- Repository variable `SUNDER_REGISTRY_API_URL` containing the Registry's absolute HTTPS origin without a path.
- Repository secret `SUNDER_REGISTRY_PUBLISH_TOKEN` containing a `sunder_pub_v1` token with an exact `package:publish` grant for every family package. Publication always sends the expected package id and cannot move `latest`.
- Repository secret `SUNDER_REGISTRY_CLI_TOKEN` containing an interactive `sunder_cli` token whose user can manage every family package. This credential is used only for coordinated dist-tag promotion and rollback.
- Repository secret `NUGET_API_KEY` authorized to publish `Sunder.Package.Agent.Protocol`.

Never commit token or API-key values.
