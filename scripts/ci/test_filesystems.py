"""Provision the case-sensitive macOS prerequisite without changing the default volume."""
from contextlib import contextmanager
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


def require_case_sensitive(root):
    probe = root / "case-probe"
    probe.write_text("probe", encoding="utf-8")
    if (root / "CASE-PROBE").exists():
        raise ValueError("The required test volume is not case-sensitive")
    probe.unlink()


@contextmanager
def case_sensitive_volume(home, log):
    work = Path(tempfile.mkdtemp(prefix="case-sensitive-tests-", dir=home))
    mount = work / "mount"
    mount.mkdir()
    image = work / "tests.sparseimage"
    attached = False

    def command(*arguments):
        result = subprocess.run(arguments, capture_output=True, text=True)
        with Path(log).open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(dict(command=arguments, exitCode=result.returncode)) + "\n")
            stream.write(result.stdout + result.stderr)
        result.check_returncode()

    try:
        command("hdiutil", "create", "-size", "512m", "-type", "SPARSE",
                "-fs", "Case-sensitive APFS", "-volname", "Sunder Test", str(image))
        command("hdiutil", "attach", "-nobrowse", "-mountpoint", str(mount), str(image))
        attached = True
        require_case_sensitive(mount)
        yield mount
    finally:
        # Never traverse a still-mounted image on failed detach, even when the
        # test process itself failed. Retain it and fail the prerequisite instead.
        if attached or mount.is_mount():
            command("hdiutil", "detach", str(mount))
        if mount.is_mount():
            raise ValueError("The case-sensitive test volume is still mounted")
        shutil.rmtree(work)
