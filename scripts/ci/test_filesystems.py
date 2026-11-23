"""Provision the case-sensitive macOS prerequisite without changing the default volume."""
from contextlib import contextmanager
import errno
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
import time


def require_case_sensitive(root):
    probe = root / "case-probe"
    probe.write_text("probe", encoding="utf-8")
    if (root / "CASE-PROBE").exists():
        raise ValueError("The required test volume is not case-sensitive")
    probe.unlink()


@contextmanager
def case_sensitive_volume(log):
    # Keep the mount out of the prepared state's recursively cleaned temp tree.
    work = Path(tempfile.mkdtemp(prefix="case-sensitive-tests-", dir=os.environ.get("RUNNER_TEMP"))).resolve()
    mount = work / "mount"
    mount.mkdir()
    image = work / "tests.sparseimage"
    attach_requested = attached = False

    def record(text):
        with Path(log).open("a", encoding="utf-8") as stream:
            stream.write(text)

    def command(*arguments, timeout=None, check=True, log_stdout=True):
        result = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout)
        record(json.dumps(dict(command=arguments, exitCode=result.returncode)) + "\n"
               + (result.stdout if log_stdout else "") + result.stderr)
        if check:
            result.check_returncode()
        return result

    def image_attachment():
        # A busy eject may already have unmounted the volume. Find the backing
        # device by the exact image path, not by an old mountpoint or disk number.
        info = command("hdiutil", "info", "-plist", timeout=5, log_stdout=False)
        rows = [row for row in plistlib.loads(info.stdout.encode("utf-8"))["images"]
                if row.get("image-path") == str(image)]
        if len(rows) > 1:
            raise ValueError(f"Ambiguous attachment for the case-sensitive test image: {image}")
        return rows[0] if rows else None

    def detach():
        try:
            command("hdiutil", "detach", str(mount))
        except subprocess.CalledProcessError as error:
            if error.returncode != errno.EBUSY:
                raise
            target = ("+f", "--", str(mount)) if mount.is_mount() else ("--", str(image))
            try:
                command("/usr/sbin/lsof", "-nP", *target, timeout=5, check=False)
            except (OSError, subprocess.TimeoutExpired) as diagnostic:
                record(f"Could not capture image or mount holders: {diagnostic}\n")
            # Disk Images may still be draining after unmount. Rebind the exact
            # image on every bounded, non-forced retry; never reuse a disk number.
            for _ in range(5):
                time.sleep(1)
                attachment = image_attachment()
                devices = [row["dev-entry"] for row in attachment["system-entities"]
                           if row.get("content-hint") == "GUID_partition_scheme"
                           and re.fullmatch(r"/dev/disk[0-9]+", row.get("dev-entry", ""))] if attachment else []
                if len(devices) != 1:
                    record(f"Cannot verify the backing device for a non-forced retry: {image}\n")
                    raise error
                try:
                    command("hdiutil", "detach", devices[0])
                    return
                except subprocess.CalledProcessError as retry:
                    if retry.returncode != errno.EBUSY:
                        raise
                    error = retry
            raise error

    try:
        command("hdiutil", "create", "-size", "512m", "-type", "SPARSE",
                "-fs", "Case-sensitive APFS", "-volname", "Sunder Test", str(image))
        attach_requested = True
        command("hdiutil", "attach", "-nobrowse", "-mountpoint", str(mount), str(image))
        attached = True
        require_case_sensitive(mount)
        yield mount
    finally:
        # Never traverse a still-mounted image on failed detach, even when the
        # test process itself failed. Retain it and fail the prerequisite instead.
        if attached or mount.is_mount():
            detach()
        if mount.is_mount() or (attach_requested and image_attachment() is not None):
            raise ValueError(f"The case-sensitive test image is still attached: {image}")
        shutil.rmtree(work)
