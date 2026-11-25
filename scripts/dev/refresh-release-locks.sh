#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 1 ]]; then
  printf 'Usage: refresh-release-locks.sh [repository-root]\n' >&2
  exit 2
fi

repository_root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
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
  --configfile "$repository_root/NuGet.Config" \
  -p:RestoreLockedMode=false \
  -p:RestorePackagesWithLockFile=true
