#!/usr/bin/env python3
"""Install the project's pinned official Windows SDK into a local tools folder."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import urllib.parse
import urllib.request
import zipfile

CS = Path(__file__).resolve().parent.parent


def install(root, cache):
    version = json.loads((CS / 'global.json').read_text('utf-8'))['sdk']['version']
    exe = root / 'dotnet.exe'
    if (root / 'sdk' / version).is_dir() and exe.is_file():
        subprocess.run([str(exe), '--version'], cwd=CS, check=True)
        return exe
    metadata_url = json.loads((CS / 'toolchain.lock.json').read_text('utf-8'))['metadataUrl']
    with urllib.request.urlopen(metadata_url, timeout=90) as response:
        metadata = json.load(response)
    candidates = []
    for release in metadata['releases']:
        sdks = release.get('sdks', []) + ([release['sdk']] if release.get('sdk') else [])
        for sdk in sdks:
            if sdk.get('version') == version:
                candidates.extend(f for f in sdk['files'] if f.get('rid') == 'win-x64' and f['name'].endswith('.zip'))
    if not candidates or len({(f['url'], f['hash'].lower()) for f in candidates}) != 1:
        raise RuntimeError('Pinned Windows SDK not uniquely present in official metadata')
    item = candidates[0]
    url = urllib.parse.urlsplit(item['url'])
    if url.scheme != 'https' or url.hostname not in {'builds.dotnet.microsoft.com', 'dotnetcli.azureedge.net', 'ci.dot.net'}:
        raise RuntimeError('Unexpected official SDK host')
    expected = item['hash'].lower()
    if len(expected) != 128 or any(c not in '0123456789abcdef' for c in expected):
        raise RuntimeError('Invalid official SHA512')
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / ('dotnet-sdk-' + version + '-win-x64.zip')

    def checksum(path):
        with path.open('rb') as stream:
            return hashlib.file_digest(stream, 'sha512').hexdigest()

    if not archive.is_file() or checksum(archive) != expected:
        partial = archive.with_suffix('.zip.part')
        print('Downloading official Windows SDK ' + version, flush=True)
        with urllib.request.urlopen(item['url'], timeout=120) as response, partial.open('wb') as out:
            total, last = 0, 0
            while block := response.read(1024 * 1024):
                out.write(block)
                total += len(block)
                if total - last >= 64 * 1024 * 1024:
                    print('SDK downloaded MiB: ' + str(total // (1024 * 1024)), flush=True)
                    last = total
        if checksum(partial) != expected:
            raise RuntimeError('Official Windows SDK SHA512 mismatch')
        os.replace(partial, archive)
    root.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as z:
        seen = set()
        for entry in z.infolist():
            p = PurePosixPath(entry.filename)
            if p.is_absolute() or '\\' in entry.filename or ':' in entry.filename or any(x in ('', '.', '..') for x in p.parts):
                raise RuntimeError('Unsafe SDK archive path')
            if stat.S_IFMT(entry.external_attr >> 16) == stat.S_IFLNK or str(p).casefold() in seen:
                raise RuntimeError('Special or duplicate SDK archive member')
            seen.add(str(p).casefold())
            target = root.joinpath(*p.parts)
            if not target.resolve().is_relative_to(root.resolve()) or target.is_symlink() or any(x.is_symlink() for x in target.parents):
                raise RuntimeError('Unsafe SDK extraction target')
            if entry.is_dir():
                continue
            data = z.read(entry)
            if target.exists():
                if target.read_bytes() != data:
                    raise RuntimeError('Existing different SDK file: ' + entry.filename)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open('xb') as out:
                out.write(data)
    (cache / ('dotnet-sdk-' + version + '-win-x64.lock.json')).write_text(json.dumps({'version': version, 'metadataUrl': metadata_url, 'url': item['url'], 'sha512': expected}, indent=2) + '\n', encoding='utf-8')
    subprocess.run([str(exe), '--version'], cwd=CS, check=True)
    return exe


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=CS.parent.parent / '.tools/dotnet')
    parser.add_argument('--cache', type=Path, default=CS.parent.parent / '.tools/downloads')
    args = parser.parse_args()
    install(args.root.resolve(), args.cache.resolve())
