#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: verify-agent-release-version.sh <release-version> <sunder-sdk-version>\n' >&2
  exit 2
fi

release_version="$1"
sdk_version="$2"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$script_dir/Sunder.Agent.ReleaseVersionVerifier.csproj"
properties=(
  -p:SunderReleaseSdkVersion="$sdk_version"
  -p:RestoreLockedMode=false
  -p:RestorePackagesWithLockFile=false
)

dotnet restore "$project" --nologo "${properties[@]}" \
  --source "${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
dotnet run --project "$project" -c Release --no-restore --nologo \
  "${properties[@]}" -- "$release_version"
