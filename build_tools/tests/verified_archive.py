"""Verify pinned build archives and publish complete extractions inside one cache."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import urllib.request
import uuid
import zipfile


def inside(root, path):
    root = Path(root).resolve()
    original = Path(path)
    if not original.is_absolute():
        original = Path.cwd() / original
    try:
        parts = original.relative_to(root).parts
    except ValueError:
        parts = None
        if os.name == "nt":
            # TEMP can use an 8.3 parent while resolve() returns its long name.
            # Match the existing root by identity, without resolving the whole
            # candidate and losing links or '..' in its remaining components.
            prefix = Path(original.anchor)
            for index, part in enumerate(original.parts[1:], 1):
                prefix = prefix / part
                try:
                    # Check each prefix before samefile() can follow a link.
                    if reparse(prefix):
                        raise ValueError("Build cache path contains a link: " + str(prefix))
                    if os.path.samefile(prefix, root):
                        parts = original.parts[index + 1:]
                        break
                except FileNotFoundError:
                    break
        if parts is None:
            raise ValueError("Build cache path is outside CacheRoot: " + str(original))
    # Inspect the original spelling before resolve() can erase a junction or
    # symlink. Walk parents in order, including components preceding '..'.
    current = root
    for part in parts:
        if part == "..":
            if current == root:
                raise ValueError("Build cache path is outside CacheRoot: " + str(original))
            current = current.parent
        else:
            current = current / part
        try:
            if reparse(current):
                raise ValueError("Build cache path contains a link: " + str(current))
        except FileNotFoundError:
            pass
    resolved = current.resolve()
    if resolved == root or os.path.commonpath([str(root), str(resolved)]) != str(root):
        raise ValueError("Build cache path is outside CacheRoot: " + str(path))
    return resolved


def reparse(path):
    attributes = path.lstat()
    return stat.S_ISLNK(attributes.st_mode) or bool(getattr(attributes, "st_file_attributes", 0) & 0x400)


def regular_tree(path):
    if reparse(path) or not path.is_dir():
        raise ValueError("Build cache directory must not be a link: " + str(path))
    for directory, folders, files in os.walk(path, followlinks=False):
        for name in folders + files:
            if reparse(Path(directory) / name):
                raise ValueError("Build cache contains a link: " + str(path))


def remove_tree(root, path):
    path = inside(root, path)
    regular_tree(path)
    shutil.rmtree(path)


def digest(stream, algorithm="sha256"):
    result = hashlib.new(algorithm)
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        result.update(block)
    return result.digest()


def verify(stream, sha256, sha512=None):
    if not re.fullmatch(r"[0-9a-fA-F]{64}", sha256) or digest(stream).hex() != sha256.lower():
        raise ValueError("Build archive SHA256 mismatch; refusing cached or downloaded bytes.")
    if sha512:
        stream.seek(0)
        if digest(stream, "sha512") != base64.b64decode(sha512, validate=True):
            raise ValueError("Build archive SHA512 mismatch; refusing cached or downloaded bytes.")
    stream.seek(0)


def members(bundle):
    result, seen = [], set()
    for entry in bundle.infolist():
        name = entry.filename.replace("\\", "/")
        relative = PurePosixPath(name)
        if relative.is_absolute() or ".." in relative.parts or ":" in name or not relative.parts or any(part.endswith((" ", ".")) for part in relative.parts):
            raise ValueError("Unsafe path in build archive.")
        if stat.S_ISLNK(entry.external_attr >> 16):
            raise ValueError("Links are not allowed in build archives.")
        if entry.is_dir():
            continue
        if name.casefold() in seen:
            raise ValueError("Duplicate path in build archive.")
        seen.add(name.casefold())
        result.append((entry, Path(*relative.parts)))
    if not result:
        raise ValueError("Build archive contains no files.")
    return result


def matches(destination, bundle, entries):
    if not destination.exists():
        return False
    regular_tree(destination)
    actual = {path.relative_to(destination) for path in destination.rglob("*") if path.is_file()}
    if actual != {relative for _, relative in entries}:
        return False
    for entry, relative in entries:
        path = destination / relative
        if path.stat().st_size != entry.file_size:
            return False
        with bundle.open(entry) as expected, path.open("rb") as existing:
            if digest(expected) != digest(existing):
                return False
    return True


def prepare_archive(cache_root, archive, destination, sha256, url, sha512=None):
    root = Path(cache_root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    archive, destination = inside(root, archive), inside(root, destination)
    if not archive.exists():
        partial = inside(root, archive.with_name(archive.name + ".download-" + uuid.uuid4().hex))
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "markdown-ru-build"})
            with urllib.request.urlopen(request, timeout=120) as response, partial.open("xb") as output:
                shutil.copyfileobj(response, output)
            with partial.open("rb") as downloaded:
                verify(downloaded, sha256, sha512)
            os.replace(partial, archive)
        finally:
            if partial.exists():
                inside(root, partial).unlink()
    # Verify even on reuse. Read the ZIP from the same handle that was hashed.
    with archive.open("rb") as stream:
        verify(stream, sha256, sha512)
        with zipfile.ZipFile(stream) as bundle:
            entries = members(bundle)
            if matches(destination, bundle, entries):
                return destination
            destination.parent.mkdir(parents=True, exist_ok=True)
            staging = inside(root, destination.with_name(destination.name + ".extract-" + uuid.uuid4().hex))
            previous = None
            staging.mkdir()
            try:
                for entry, relative in entries:
                    target = inside(staging, staging / relative)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.open(entry) as source, target.open("xb") as output:
                        shutil.copyfileobj(source, output)
                # ZIP reads validate CRC before any complete directory is published.
                if destination.exists():
                    if matches(destination, bundle, entries):
                        return destination
                    regular_tree(destination)
                    previous = inside(root, destination.with_name(destination.name + ".invalid-" + uuid.uuid4().hex))
                    destination.rename(previous)
                try:
                    staging.rename(destination)
                except FileExistsError:
                    if not matches(destination, bundle, entries):
                        raise
            finally:
                if staging.exists():
                    remove_tree(root, staging)
                if previous is not None and previous.exists():
                    remove_tree(root, previous)
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--cache-root", required=True)
    parser.add_argument("--destination", required=True)
    args = parser.parse_args()
    with open(args.lock, encoding="utf-8-sig") as source:
        lock = json.load(source)
    entries = [entry for entry in lock["archives"] if entry["id"] == args.artifact]
    if lock["schema"] != 1 or len(entries) != 1:
        raise ValueError("Missing or ambiguous build archive lock entry.")
    entry = entries[0]
    root = Path(args.cache_root).resolve()
    candidates = [inside(root, root / name) for name in entry["cacheNames"]]
    archive = next((path for path in candidates if path.exists()), candidates[0])
    print(prepare_archive(root, archive, root / args.destination, entry["sha256"], entry["url"], entry.get("sha512")))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Verified build cache failed: " + str(error), file=sys.stderr)
        sys.exit(1)
