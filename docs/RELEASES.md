# Agent Family Releases

Agent releases are coordinated family releases. One `agent/vX.Y.Z` tag builds and publishes `Sunder.Package.Agent.Protocol` plus every `sunderpkg` entry in `packages.json` from one Agent commit, one Core commit, and one version.

## Core Coordination

Agent CI and release jobs resolve `Younics/sunder-core` `refs/heads/main` once at workflow start. The resolved full commit SHA is passed to every Core checkout so one run remains internally consistent even if `main` moves while the workflow is running.

Cross-repository changes require this order:

1. Land the compatible Core changes on Core `main`.
2. Publish the Core developer packages required by `Directory.Packages.props` and regenerate Agent release locks against those exact remote packages.
3. Commit the coordinated Agent changes.
4. Confirm Agent smoke CI reports and passes against the expected resolved Core SHA.
5. Set `packages.json` `releaseVersion`, then create and push `agent/v<releaseVersion>` from the Agent release commit.

Do not start coordinated Agent CI before the required Core changes reach `main`; the workflow intentionally consumes the latest Core `main` commit available when the run starts. Do not create an Agent release tag until the matching `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, and `Sunder.Package.Build` versions are available from NuGet.org. The release workflow verifies that prerequisite and builds the Protocol once against the locked remote SDK before rebuilding the family from the resolved Core source commit.

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
6. Creates a draft GitHub release and publishes Protocol to NuGet.org.
7. Publishes every Registry version with `setLatest=false`, then downloads and SHA-256 verifies each artifact.
8. Moves all runtime package `latest` tags for stable releases or `preview` tags for prereleases only after every immutable artifact verifies.
9. Publishes the draft GitHub release.

A failure can leave verified immutable NuGet or Registry versions without promoted Registry tags. Rerunning the same tag may resolve a newer Core `main` commit and is safe only when the resulting remote bytes match; the workflow refuses a mismatched existing artifact.

## Repository Configuration

The release workflow requires:

- Repository variable `SUNDER_REGISTRY_API_URL` containing the Registry origin.
- Repository secret `SUNDER_REGISTRY_TOKEN` containing a Registry CLI token authorized to publish and manage every family package.
- Repository secret `NUGET_API_KEY` authorized to publish `Sunder.Package.Agent.Protocol`.

Never commit token or API-key values.
