# Agent Family Releases

Agent releases are coordinated family releases. One `agent/vX.Y.Z` tag builds and publishes `Sunder.Package.Agent.Protocol` plus every `sunderpkg` entry in `packages.json` from one Agent commit, one Core commit, and one version.

## Core Coordination

Agent release CI resolves the immutable `sdk/v<CORE_SDK_VERSION>` release, verifies that the tag, Core release provenance, GitHub release assets, NuGet canonical content, NuGet repository signatures, package repository metadata, and every Agent release lock identify one Core commit and package version, and passes that full commit SHA to every Core test checkout. The Core tag, commit, and package version are recorded in `native-test-evidence.json`, the draft release notes, and `release-provenance.json`. Recovery accepts only those same materials.

Cross-repository changes require this order:

1. Land the compatible Core changes and publish the matching immutable Core `sdk/v*` release.
2. Publish the Core developer packages required by `Directory.Packages.props` and regenerate every Agent release lock against that exact remote package version.
3. Commit the coordinated Agent changes.
4. Confirm Agent smoke CI passes and the release workflow will resolve the expected Core SDK tag SHA.
5. Set `packages.json` `releaseVersion`, then create and push `agent/v<releaseVersion>` from the Agent release commit.

Do not create an Agent release tag until the matching `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, and `Sunder.Package.Build` packages and their public Core SDK release are final. Agent production bytes are never rebuilt from a Core source checkout. Every Agent restore uses `SunderUseSourceSdk=false`, locked mode, and NuGet.org; the Core checkout is used only to run the exact provenance-matched Runtime Host, Package Format, App, and Core metric gates.

## Native Build Boundary

The release has one native job for each supported RID: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each job must run on that exact native architecture, builds exactly the 15 Worker leaves, and creates current-RID smoke archives without cross-compiling another release RID.

Windows apphosts are Authenticode-signed with Azure Trusted Signing. macOS apphosts are timestamped Developer ID binaries. Signing runs through Core's `SunderPackageWorkerSigningTargets` pre-index hook over its private publish-closure copy; the job then independently verifies the platform signature and signer identity. Linux apphosts remain unsigned. The native job launches all 15 resulting workers through a real Runtime Host lifecycle and records Core-enforced archive size, current-RID installed size, startup latency, process-count, and steady-RSS evidence for each worker. The same gate exercises owner unary and server-stream RPC, MCP callback dispatch, OpenAI host auth, both Agent SQLite baselines, forced worker crash recovery, disable/enable retirement, process cleanup, and temporary-directory cleanup.

Each job uploads an opaque tarball so Unix executable modes and signed bytes survive Actions artifact transfer. Fan-in verifies every file hash and metric record, extracts all six sets, builds only App leaves and Protocol against locked remote Core packages, and invokes aggregate package targets with `BuildProjectReferences=false`. Core's aggregate task validates the exact target leaves before producing the immutable universal archives. CI additionally compares every final RID apphost hash with its native signed-leaf hash.

## Stable Boundary

Before a stable tag, finalize the Protocol API ledger by moving accepted API entries into `PublicAPI.Shipped.txt`. `PublicAPI.Unshipped.txt` may retain `#nullable enable` and comments, but no API entries. The release prepare job enforces this condition.

The Agent 2.x line uses `[2.0.0,3.0.0)` for `Sunder.Package.Agent.Protocol` and `>=2.0.0 <3.0.0` for runtime dependencies. `agent/v2.0.0-*` is therefore rejected because it precedes the stable lower bound. The Protocol package keeps its Sunder SDK dependency on the SDK's independent supported minor line.

## Publication Order

The release workflow:

1. Verifies the exact Core SDK tag/package provenance and builds/tests the complete Agent solution once in locked remote-package mode.
2. Runs the six native jobs described above, including required platform signing and Core metric budgets.
3. Fans in only those exact native leaves and packs one Protocol NuGet package plus all 15 immutable universal archives. CI caps Protocol at 16 MiB, each runtime archive at 256 MiB compressed and 640 MiB indexed payload, and the compressed family at 4 GiB; it also requires the complete six-RID SQLite native matrix.
4. Validates exact aggregate bytes with provenance-matched `Sunder.Package.Format`, reruns the complete family install/activate/unload/reload/reinstall lifecycle, and transfers the Runtime-produced Agent UI snapshot into the real App package host.
5. Uploads one Actions artifact containing exactly the tested 16 publishable artifacts and `native-test-evidence.json`; the publish job downloads that artifact by exact name and verifies all hashes before any external write.
6. Creates a draft GitHub release whose notes and evidence record the exact Agent commit and Core SDK tag/package/commit, then publishes Protocol to NuGet.org.
7. Publishes every Registry version with a package-scoped publish token and `setLatest=false`, refetches version details, and verifies every projection and canonical byte identity.
8. Moves all runtime package `latest` tags for stable releases or `preview` tags for prereleases only after every immutable artifact verifies.
9. Publishes the draft GitHub release.

A failure can leave verified immutable NuGet or Registry versions without promoted Registry tags. Once NuGet publication may have started, the workflow preserves the draft release and its evidence rather than deleting or replacing it. Rerun the same tag to recover: the workflow requires the same Agent commit, Core SDK tag/package/commit, six native runs, release asset inventory, and bytes before continuing. A mismatch stops recovery without overwriting evidence.

## Repository Configuration

The release workflow requires:

- GitHub environment `sunder-agent-release-signing`, protected as appropriate for production signing.
- Optional repository variables `SUNDER_AGENT_RUNNER_WIN_X64`, `SUNDER_AGENT_RUNNER_WIN_ARM64`, `SUNDER_AGENT_RUNNER_LINUX_X64`, `SUNDER_AGENT_RUNNER_LINUX_ARM64`, `SUNDER_AGENT_RUNNER_OSX_X64`, and `SUNDER_AGENT_RUNNER_OSX_ARM64`. Each value is a JSON array of hosted, larger-runner, or self-hosted labels. Defaults use `windows-latest`, `windows-11-arm`, `ubuntu-24.04`, `ubuntu-24.04-arm`, `macos-15-intel`, and `macos-14` respectively.
- Azure OIDC values `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`; Trusted Signing values `AZURE_TRUSTED_SIGNING_ENDPOINT`, `AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`, and `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`; and required verification pin `WINDOWS_PUBLISHER_SUBJECT`.
- Apple secrets `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_KEYCHAIN_PASSWORD`, `APPLE_SIGN_APP_IDENTITY`, and `APPLE_TEAM_ID`.
- Repository variable `SUNDER_REGISTRY_API_URL` containing the Registry's absolute HTTPS origin without a path.
- Repository secret `SUNDER_REGISTRY_PUBLISH_TOKEN` containing a `sunder_pub_v1` token with an exact `package:publish` grant for every family package. Publication always sends the expected package id and cannot move `latest`.
- Repository secret `SUNDER_REGISTRY_CLI_TOKEN` containing an interactive `sunder_cli` token whose user can manage every family package. This credential is used only for coordinated dist-tag promotion and rollback.
- Repository secret `NUGET_API_KEY` authorized to publish `Sunder.Package.Agent.Protocol`.

Never commit signing material, token, password, or API-key values.
