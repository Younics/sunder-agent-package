#!/usr/bin/env bash
set -euo pipefail

mode="${1:---write}"
if [[ "$mode" == "--write" || "$mode" == "--check" ]]; then
  shift
else
  mode="--write"
fi

repository_root="${1:-.}"
repository_root="$(cd "$repository_root" && pwd)"
test_project="$repository_root/tests/Sunder.Package.Agent.Tests/Sunder.Package.Agent.Tests.csproj"
source_lock_path="obj/compatibility-source/packages.lock.json"

# Source-reference compatibility checks must not rewrite platform-specific Core
# release locks. The generated candidate still hash-binds those tracked locks.
dotnet restore "$test_project" \
  --force-evaluate \
  -p:SunderUseSourceSdk=true \
  -p:RestorePackagesWithLockFile=true \
  -p:RestoreLockedMode=false \
  -p:NuGetLockFilePath="$source_lock_path"

if [[ "$mode" == "--write" ]]; then
  SUNDER_UPDATE_AGENT_COMPATIBILITY_CANDIDATE=1 \
    dotnet test "$test_project" \
    --no-restore \
    -m:1 \
    -p:SunderUseSourceSdk=true \
    -p:RestorePackagesWithLockFile=true \
    -p:RestoreLockedMode=false \
    -p:NuGetLockFilePath="$source_lock_path" \
    --filter FullyQualifiedName~AgentCompatibilityCandidateTests
  printf 'Updated Agent compatibility candidate and RPC migration baseline\n'
else
  dotnet test "$test_project" \
    --no-restore \
    -m:1 \
    -p:SunderUseSourceSdk=true \
    -p:RestorePackagesWithLockFile=true \
    -p:RestoreLockedMode=false \
    -p:NuGetLockFilePath="$source_lock_path" \
    --filter FullyQualifiedName~AgentCompatibilityCandidateTests
  printf 'Verified Agent compatibility candidate and RPC migration baseline\n'
fi
