#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 10 ]]; then
  printf 'Usage: record-native-leaf-evidence.sh <rid> <version> <agent-commit> <core-tag> <core-commit> <core-package-version> <archive-directory> <metrics-directory> <output-directory> <repository-root>\n' >&2
  exit 2
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 -B "$script_dir/native-family.py" record "$@"
