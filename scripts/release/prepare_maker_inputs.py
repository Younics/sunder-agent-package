#!/usr/bin/env python3
"""Prepare Maker's authoring feed, selected catalog, and shipped App/Host."""
import argparse
import hashlib
import json
from pathlib import Path

import maker_public_feed
import published_app
import public_platform_catalog


def prepare(output, *, host_only=False):
    output = Path(output).absolute()
    if output.exists() or output.is_symlink():
        raise ValueError("Maker input output must be new; retained candidates are never overwritten")
    output.mkdir(parents=True)
    feed = output / "feed"
    if host_only:
        feed.mkdir()
    packages = [] if host_only else maker_public_feed.prepare(output)
    catalog = public_platform_catalog.fetch(maker_public_feed.ROOT, output)
    published_app.prepare(output / "distribution")
    app = json.loads((output / "distribution" / "app-input.json").read_text(encoding="utf-8"))
    (output / "inputs.json").write_text(json.dumps(dict(schemaVersion=3,
        packages=packages, platformCatalog=file_record(catalog, output), app=app), indent=2) + "\n", encoding="utf-8")
    return feed


def file_record(path, root):
    return dict(path=path.relative_to(root).as_posix(), size=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--host-only", action="store_true", help="Prepare the exact App/catalog without downloading Maker's authoring NuGets.")
    args = parser.parse_args()
    print(prepare(args.output, host_only=args.host_only))
