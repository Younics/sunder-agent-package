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

dotnet restore "$test_project" \
  --locked-mode \
  -p:SunderUseSourceSdk=true

if [[ "$mode" == "--write" ]]; then
  SUNDER_UPDATE_AGENT_COMPATIBILITY_CANDIDATE=1 \
    dotnet test "$test_project" \
    --no-restore \
    -m:1 \
    -p:SunderUseSourceSdk=true \
    --filter FullyQualifiedName~AgentCompatibilityCandidateTests
  printf 'Updated compatibility/agent-compatibility-candidate.json\n'
else
  dotnet test "$test_project" \
    --no-restore \
    -m:1 \
    -p:SunderUseSourceSdk=true \
    --filter FullyQualifiedName~AgentCompatibilityCandidateTests
  printf 'Verified compatibility/agent-compatibility-candidate.json\n'
fi
