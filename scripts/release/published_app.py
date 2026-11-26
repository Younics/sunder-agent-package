"""Stage the shipped App and its bundled Host for Agent integration tests."""
import argparse
from pathlib import Path, PurePosixPath
import platform
import shutil
import stat
import subprocess
import sys
from zipfile import ZipFile

from ci_platforms import RIDS
import release_inputs as inputs

ROOT = Path(__file__).resolve().parents[2]
DISTRIBUTION = "https://github.com/Younics/sunder-core/releases/download"
MAX_ARCHIVE_BYTES = 384 * 1024**2
MAX_PAYLOAD_BYTES = 768 * 1024**2


def current_rid():
    os_name = {"darwin": "osx", "linux": "linux", "win32": "win"}[sys.platform]
    architecture = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}[platform.machine().lower()]
    return f"{os_name}-{architecture}"


def selection(rid, root=ROOT, *, installed=False):
    policy = inputs.load(Path(root) / "eng/ci" / ("installed-app-release.json" if installed else "app-release.json"))
    inputs.closed(policy, "version producerCommit artifacts")
    version = policy["version"]
    channel = "stable" if inputs.version(version)[1] else "preview"
    inputs.require("+" not in version and set(policy["artifacts"]) == set(RIDS), "Invalid App release inventory")
    row = policy["artifacts"][rid]
    inputs.closed(row, "size sha256")
    inputs.require(inputs.positive(row["size"]) <= MAX_ARCHIVE_BYTES, "App image exceeds its byte bound")
    extension = {"osx": "dmg", "linux": "AppImage"}.get(rid.split("-")[0])
    name = (f"Sunder-app-{rid}-{channel}.{extension}" if extension
            else f"Sunder-{version}-app-{rid}-{channel}-full.nupkg")
    return dict(name=name, url=f"{DISTRIBUTION}/app/v{version}/{name}",
                version=version, producerCommit=inputs.digest(policy["producerCommit"], 40),
                size=row["size"], sha256=inputs.digest(row["sha256"]))


def extract_full(archive, destination):
    """Only project the Velopack payload. Never run its installer/update hooks."""
    with ZipFile(archive) as full:
        members, size = {}, 0
        for entry in full.infolist():
            if not entry.filename.startswith("lib/app/") or entry.is_dir():
                continue
            name = entry.filename.removeprefix("lib/app/")
            inputs.safe_relative(name)
            path = PurePosixPath(name)
            inputs.require(not any(char in name for char in "\\:%") and name.casefold() not in members
                           and stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG),
                           "Invalid or ambiguous Velopack payload path")
            members[name.casefold()] = (path, entry)
            size += entry.file_size
        inputs.require(0 < size <= MAX_PAYLOAD_BYTES, "Invalid Velopack payload size")
        for path, entry in members.values():
            target = destination / path
            target.parent.mkdir(parents=True, exist_ok=True)
            with full.open(entry) as content, target.open("xb") as output:
                shutil.copyfileobj(content, output)
            if entry.external_attr >> 16 & 0o111 or target.name in ("Sunder.App", "Sunder.Runtime.Host", "sunder"):
                target.chmod(0o755)


def extract_macos(archive, destination):
    # The DMG preserves the signed bundle layout and extended attributes.
    # Reconstructing the bundle from its updater ZIP does not.
    mount = destination / "mount"
    mount.mkdir()
    subprocess.run(["hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", str(mount), str(archive)],
                   check=True, timeout=120)
    try:
        app = mount / "Sunder.app"
        target = destination / "Sunder.app"
        subprocess.run(["ditto", str(app), str(target)], check=True, timeout=120)
        subprocess.run(["codesign", "--verify", "--strict", "-R=anchor apple generic and certificate leaf[subject.OU] = KLL23H4DRP",
                        str(target)], check=True, timeout=90)
        subprocess.run(["spctl", "--assess", "--type", "execute", str(target)], check=True, timeout=90)
        return target / "Contents/MacOS"
    finally:
        subprocess.run(["hdiutil", "detach", str(mount)], check=True, timeout=60)


def prepare(destination, rid=None, *, installed=False):
    rid = rid or current_rid()
    inputs.require(rid == current_rid(), "App integration must use the runner's own native ABI")
    selected = selection(rid, installed=installed)
    destination = Path(destination).absolute()
    destination.mkdir(parents=True)
    archive = destination / selected["name"]
    env = inputs.isolated_env(destination / "home", destination / "home/contracts")
    subprocess.run(["curl", "--disable", "--fail", "--silent", "--show-error", "--location",
                    "--proto", "=https", "--proto-redir", "=https", "--max-time", "180",
                    "--max-filesize", str(selected["size"]), "--output", str(archive), selected["url"]],
                   env=env, check=True, timeout=185)
    inputs.require(inputs.regular(archive).stat().st_size == selected["size"]
                   and inputs.file_sha(archive) == selected["sha256"], "App release image differs from the reviewed publication")
    if rid.startswith("osx-"):
        app = extract_macos(archive, destination)
    elif rid.startswith("linux-"):
        # The shipped AppImage can extract its own payload without FUSE,
        # installation, or starting the desktop. Verify its hash before running it.
        archive.chmod(0o755)
        subprocess.run([str(archive), "--appimage-extract"], cwd=destination, env=env,
                       stdout=subprocess.DEVNULL, check=True, timeout=120)
        assembly, = (destination / "squashfs-root").rglob("Sunder.App.dll")
        app = inputs.regular(assembly).parent
    else:
        app = destination / "app"
        extract_full(archive, app)
    inputs.regular(app / "Sunder.App.dll")
    host, = (app / "RuntimeHost").rglob("Sunder.Runtime.Host.dll")
    inputs.regular(host)
    receipt = dict(schemaVersion=1, rid=rid, app=app.relative_to(destination).as_posix(),
                   host=host.parent.relative_to(destination).as_posix(), artifact=selected)
    (destination / "app-input.json").write_bytes(inputs.canonical(receipt) + b"\n")
    return app


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--rid", choices=RIDS)
    args = parser.parse_args()
    print(prepare(args.destination, args.rid))
