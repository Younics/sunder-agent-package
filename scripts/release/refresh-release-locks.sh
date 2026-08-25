#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  printf 'Usage: refresh-release-locks.sh <core-sdk-version> <core-package-feed> [repository-root]\n' >&2
  exit 2
fi

version="$1"
core_package_feed="$(cd "$2" && pwd -P)"
repository_root="${3:-.}"
repository_root="$(cd "$repository_root" && pwd -P)"

for command_name in dotnet mktemp rm; do
  command -v "$command_name" >/dev/null 2>&1 \
    || { printf '%s is required to refresh release locks.\n' "$command_name" >&2; exit 127; }
done

work_directory="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-release-locks.XXXXXX")"
cleanup() {
  rm -rf "$work_directory"
}
trap cleanup EXIT

dotnet restore "$repository_root/Sunder.AgentPackage.slnx" \
  --force-evaluate \
  --no-http-cache \
  --artifacts-path "$work_directory/artifacts" \
  --packages "$work_directory/packages" \
  --source "$core_package_feed" \
  --source https://api.nuget.org/v3/index.json \
  -p:SunderUseSourceSdk=false \
  -p:RestoreLockedMode=false \
  -p:RestorePackagesWithLockFile=true

bash "$repository_root/scripts/release/verify-release-locks.sh" \
  "$version" \
  "$repository_root" \
  "$core_package_feed"
