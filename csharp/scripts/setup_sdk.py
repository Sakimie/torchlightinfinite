#!/usr/bin/env python3
"""Install the locked official SDK only after SHA512 verification."""
import hashlib
import json
from pathlib import Path
import subprocess
import tarfile
import urllib.request

HERE = Path(__file__).resolve().parent.parent
lock = json.loads((HERE / 'toolchain.lock.json').read_text())
sdk_root = Path('/workspace/.dotnet')
cache = HERE.parent / '.cache' / 'dotnet'
cache.mkdir(parents=True, exist_ok=True)
archive = cache / ('dotnet-sdk-' + lock['sdk'] + '-linux-x64.tar.gz')
if not archive.exists() or hashlib.sha512(archive.read_bytes()).hexdigest().lower() != lock['sha512'].lower():
    with urllib.request.urlopen(lock['url'], timeout=60) as source, archive.open('wb') as output:
        while chunk := source.read(1024 * 1024):
            output.write(chunk)
if hashlib.sha512(archive.read_bytes()).hexdigest().lower() != lock['sha512'].lower():
    raise RuntimeError('Official SDK SHA512 verification failed')
if not (sdk_root / 'sdk' / lock['sdk']).is_dir():
    sdk_root.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, 'r:gz') as contents:
        contents.extractall(sdk_root, filter='data')
subprocess.run([str(sdk_root / 'dotnet'), '--version'], cwd=HERE, check=True)
