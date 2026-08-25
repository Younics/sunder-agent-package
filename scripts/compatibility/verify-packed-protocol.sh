#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 3 ]]; then
  printf 'Usage: verify-packed-protocol.sh <protocol-nupkg> [version] [repository-root]\n' >&2
  exit 2
fi

package_path="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
version="${2:-2.0.0}"
repository_root="${3:-.}"
repository_root="$(cd "$repository_root" && pwd)"
fixture="$repository_root/fixtures/PackedProtocolConsumer/PackedProtocolConsumer.csproj"

[[ -f "$package_path" ]] || { printf 'Packed Protocol was not found: %s\n' "$package_path" >&2; exit 1; }
[[ -f "$fixture" ]] || { printf 'Packed Protocol consumer was not found: %s\n' "$fixture" >&2; exit 1; }
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] \
  || { printf 'Protocol version is not valid SemVer: %s\n' "$version" >&2; exit 2; }

work="$(mktemp -d "${TMPDIR:-/tmp}/sunder-agent-packed-protocol.XXXXXX")"
trap 'rm -rf "$work"' EXIT

sources=(--source "$(dirname "$package_path")")
core_feed="$repository_root/../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk/bin/Debug"
if [[ -d "${SUNDER_CORE_PACKAGE_SOURCE:-$core_feed}" ]]; then
  sources+=(--source "${SUNDER_CORE_PACKAGE_SOURCE:-$core_feed}")
fi
sources+=(--source "${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}")

properties=(
  -p:AgentProtocolPackageVersion="$version"
  -p:SunderUseSourceSdk=true
  -p:RestorePackagesPath="$work/packages"
  -p:BaseIntermediateOutputPath="$work/obj/"
  -p:BaseOutputPath="$work/bin/"
)

dotnet restore "$fixture" --force-evaluate "${properties[@]}" "${sources[@]}"
dotnet build "$fixture" -c Release --no-restore "${properties[@]}"

restored_dll="$work/packages/sunder.package.agent.protocol/$version/lib/net10.0/Sunder.Package.Agent.Protocol.dll"
[[ -f "$restored_dll" ]] || { printf 'Restored Protocol assembly was not found: %s\n' "$restored_dll" >&2; exit 1; }
unzip -p "$package_path" lib/net10.0/Sunder.Package.Agent.Protocol.dll > "$work/packed-protocol.dll"
cmp "$work/packed-protocol.dll" "$restored_dll"

dotnet "$work/bin/Release/net10.0/PackedProtocolConsumer.dll"
