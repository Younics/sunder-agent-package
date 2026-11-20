#!/usr/bin/env python3
"""Bind nested script processes to native Bash, including Git Bash on Windows."""
from functools import lru_cache
import os
from pathlib import Path
import shutil
import subprocess
import sys

VARIABLE = "SUNDER_BASH_EXECUTABLE"


@lru_cache(maxsize=None)
def verify(path, windows):
    result = subprocess.run([path, "--noprofile", "--norc", "-c", 'printf "%s\\n" "$BASH_VERSION" "$OSTYPE"'],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
    lines = result.stdout.splitlines()
    if result.returncode or len(lines) != 2 or not lines[0] or (windows and lines[1] not in ("msys", "cygwin")):
        raise ValueError(f"Expected {'native Windows Bash (msys/cygwin)' if windows else 'Bash'} at {path}; "
                         f"exit={result.returncode}, stdout={result.stdout!r}, stderr={result.stderr!r}")


def executable():
    windows = sys.platform == "win32"
    configured = os.environ.get(VARIABLE)
    if configured:
        candidates = [Path(configured)]
    elif windows:
        # Windows CreateProcess can find System32's WSL launcher before PATH.
        # Locate Bash within Git instead of searching for an unqualified bash.exe.
        git = shutil.which("git")
        roots = list(Path(git).resolve().parents)[:3] if git else []
        roots += [Path(os.environ[key]) / "Git" for key in ("ProgramFiles", "ProgramFiles(x86)") if os.environ.get(key)]
        candidates = [root / relative for root in roots for relative in ("bin/bash.exe", "usr/bin/bash.exe")]
    else:
        found = shutil.which("bash")
        candidates = [Path(found)] if found else []
    for path in candidates:
        if path.is_absolute() and path.is_file():
            value = str(path.resolve())
            verify(value, windows)
            return value
    raise ValueError(f"Native Bash executable unavailable; set {VARIABLE} to an absolute "
                     f"{'Git Bash' if windows else 'Bash'} executable path")


def command(*arguments):
    return [executable(), "--noprofile", "--norc", *map(str, arguments)]


if __name__ == "__main__":
    try:
        print("Nested script Bash: " + executable())
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
