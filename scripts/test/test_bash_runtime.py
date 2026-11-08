"""Native Bash resolution and real nested script/argument/exit-code boundaries."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
import bash_runtime as bash


class BashRuntimeTests(unittest.TestCase):
    def setUp(self):
        scope = tempfile.TemporaryDirectory(prefix="Git with spaces ")
        self.addCleanup(scope.cleanup)
        self.root = Path(scope.name)
        bash.verify.cache_clear()
        self.addCleanup(bash.verify.cache_clear)

    def test_windows_resolves_git_bash_without_searching_for_wsl_launcher(self):
        git = self.root / "cmd/git.exe"
        native = self.root / "bin/bash.exe"
        for path in (git, native):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.touch()
        with patch.dict(os.environ, {}, clear=True), patch.object(sys, "platform", "win32"), \
             patch.object(bash.shutil, "which", return_value=str(git)) as which, patch.object(bash, "verify") as verify:
            self.assertEqual(str(native.resolve()), bash.executable())
            which.assert_called_once_with("git")
            verify.assert_called_once_with(str(native.resolve()), True)
            native.unlink()
            with self.assertRaisesRegex(ValueError, bash.VARIABLE): bash.executable()

    def test_explicit_binding_must_be_absolute_and_existing_without_fallback(self):
        for value in ("bash", str(self.root / "absent.exe"), str(self.root)):
            with self.subTest(value=value), patch.dict(os.environ, {bash.VARIABLE: value}), \
                 patch.object(bash.shutil, "which", side_effect=AssertionError("explicit binding must not fall back")), \
                 self.assertRaisesRegex(ValueError, bash.VARIABLE):
                bash.executable()

    def test_windows_probe_rejects_wsl_and_accepts_native_git_bash_variants(self):
        for status, stdout in ((1, "Windows Subsystem for Linux has no installed distributions.\n"), (0, "5.2.0\nlinux-gnu\n")):
            with self.subTest(stdout=stdout), patch.object(bash.subprocess, "run", return_value=subprocess.CompletedProcess([], status, stdout, "probe stderr")):
                with self.assertRaisesRegex(ValueError, "native Windows Bash.*stdout=.*stderr="):
                    bash.verify("fixture-bash", True)
        for operating_system in ("msys", "cygwin"):
            with self.subTest(operating_system=operating_system), patch.object(bash.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "5.3.15\n" + operating_system + "\n", "")):
                bash.verify("fixture-bash-" + operating_system, True)

    def test_real_nested_process_preserves_binding_paths_arguments_and_failure(self):
        executable = bash.executable()
        script = self.root / "script with spaces.sh"
        script.write_bytes(b'printf "<%s>\\n" "$1"\nexit 23\n')
        code = ('import sys; sys.path.insert(0, sys.argv[1]); import bash_runtime; import subprocess; '
                'sys.exit(subprocess.run(bash_runtime.command(sys.argv[2], sys.argv[3])).returncode)')
        result = subprocess.run([sys.executable, "-B", "-c", code, str(Path(bash.__file__).parent), str(script), "argument with spaces"],
                                env={**os.environ, bash.VARIABLE: executable}, capture_output=True, text=True)
        self.assertEqual(23, result.returncode, result.stdout + result.stderr)
        self.assertEqual("<argument with spaces>\n", result.stdout)


if __name__ == "__main__": unittest.main()
