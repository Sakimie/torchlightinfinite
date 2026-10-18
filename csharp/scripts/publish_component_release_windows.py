#!/usr/bin/env python3
"""Publish existing signed components from Windows using Git Credential Manager.

Keeps the original release checks and publication order. Only the developer
verifier is built; frozen application archives and signatures remain unchanged.
"""
import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('talos_component_publisher', HERE / 'publish_component_release.py')
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)
SOURCE_REPO = publisher.ROOT


def adapt(dotnet):
    if os.name != 'nt':
        raise RuntimeError('This entry point requires Windows')
    dotnet = dotnet.resolve()
    if not dotnet.is_file():
        raise RuntimeError('Install the pinned Windows SDK before publishing')
    tools = publisher.ROOT.parent / '.tools'
    temp = tools / 'temp'
    temp.mkdir(parents=True, exist_ok=True)

    def run(args, cwd=None, env=None):
        args = [str(x) for x in args]
        if args[0] == '/workspace/.dotnet/dotnet':
            args[0] = str(dotnet)
        actual_env = os.environ.copy()
        if env:
            actual_env.update(env)
        actual_env.update({'DOTNET_ROOT': str(dotnet.parent),
                           'DOTNET_CLI_HOME': str(tools / 'dotnet-home'),
                           'TMPDIR': str(temp), 'TEMP': str(temp), 'TMP': str(temp),
                           'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1',
                           'DOTNET_CLI_UI_LANGUAGE': 'en',
                           'GIT_TERMINAL_PROMPT': '0', 'GCM_INTERACTIVE': 'never'})
        result = subprocess.run(args, cwd=cwd, env=actual_env, capture_output=True,
                                text=True, encoding='utf-8', errors='replace')
        if result.returncode:
            raise RuntimeError('Command failed: ' + Path(args[0]).name + ' (exit ' +
                               str(result.returncode) + '; authentication diagnostics omitted)')
        return result.stdout.strip()

    @contextmanager
    def publication_lock(out):
        import msvcrt
        with (out / 'component-publish.lock').open('a+b') as lock:
            if lock.tell() == 0:
                lock.write(b'\0')
                lock.flush()
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            try:
                yield
            finally:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)

    credential_cache = []

    def credential():
        # Ask the configured provider for this exact authorized host. Credentials
        # remain in memory and are never printed, written to disk, or put in argv.
        if credential_cache:
            return credential_cache[0]
        env = os.environ.copy()
        env.update({'GIT_TERMINAL_PROMPT': '0', 'GCM_INTERACTIVE': 'never'})
        result = subprocess.run(['git', 'credential', 'fill'], cwd=SOURCE_REPO,
                                input='protocol=https\nhost=github.com\n\n',
                                text=True, capture_output=True, env=env)
        if result.returncode:
            raise RuntimeError('Git Credential Manager login is required for the GitHub release API')
        values = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
        if values.get('protocol') != 'https' or values.get('host') != 'github.com' or not values.get('password'):
            raise RuntimeError('Credential provider did not return an authorized GitHub credential')
        credential_cache.append(values['password'])
        return credential_cache[0]

    def api(endpoint, method='GET', value=None, missing_ok=False):
        prefix = 'repos/' + publisher.REPO
        if endpoint != prefix and not endpoint.startswith(prefix + '/'):
            raise RuntimeError('Unexpected GitHub API repository')
        url = 'https://api.github.com/' + endpoint
        headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'TalosHelper-Windows-Publisher',
                   'X-GitHub-Api-Version': '2022-11-28'}
        headers['Authorization'] = 'Bearer ' + credential()
        headers['Cache-Control'] = 'no-cache'
        body = None if value is None else json.dumps(value, ensure_ascii=False).encode('utf-8')
        if body is not None:
            headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, response_headers, newurl):
                return None
        opener = urllib.request.build_opener(NoRedirect())
        try:
            with opener.open(request, timeout=45) as response:
                if urllib.parse.urlsplit(response.geturl()).hostname != 'api.github.com':
                    raise RuntimeError('Unexpected GitHub API redirect')
                return json.load(response)
        except urllib.error.HTTPError as error:
            if missing_ok and error.code == 404:
                return None
            # Never include provider output, request headers or an API error body.
            raise RuntimeError('GitHub API failed: ' + method + ' ' + endpoint + ' HTTP ' + str(error.code)) from None

    def prepare_checkout(version):
        work = publisher.ROOT.parent / 'scratch' / ('talos-component-publish-' + version)
        remote = publisher.authorized_remote(SOURCE_REPO)
        if not (work / '.git').exists():
            if work.exists():
                raise RuntimeError('Unexpected existing publication folder')
            work.parent.mkdir(parents=True, exist_ok=True)
            run(['git', 'clone', '--config', 'core.autocrlf=false', '--filter=blob:none', '--sparse', '--single-branch', '--branch',
                 publisher.BRANCH, remote, str(work)])
            run(['git', 'sparse-checkout', 'set', '--no-cone', '/latest.json', '/csharp/latest.json',
                 '/csharp/releases/' + version + '/'], cwd=work)
            for key in ('user.name', 'user.email'):
                value = run(['git', 'config', '--get', key], cwd=SOURCE_REPO)
                run(['git', 'config', key, value], cwd=work)
            run(['git', 'config', 'core.autocrlf', 'false'], cwd=work)
        if work.is_symlink() or (work / '.git').is_symlink():
            raise RuntimeError('Unsafe publication checkout')
        publisher.authorized_remote(work)
        if run(['git', 'status', '--porcelain'], cwd=work):
            raise RuntimeError('Publication checkout contains local changes; refusing to discard them')
        run(['git', 'fetch', 'origin', 'refs/heads/' + publisher.BRANCH], cwd=work)
        run(['git', 'merge', '--ff-only', 'FETCH_HEAD'], cwd=work)
        if publisher.digest(publisher.plain(work / 'latest.json')) != publisher.OLD_SHA:
            raise RuntimeError('Existing Python stable channel differs')
        return work

    original_public_check = publisher.public_check

    def public_check(url, expected):
        # GitHub's public raw CDN can briefly cache the previous channel/404.
        for attempt in range(5):
            try:
                return original_public_check(url, expected)
            except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, RuntimeError):
                if attempt == 4:
                    raise
                print('Waiting for public GitHub bytes: ' + url.rsplit('/', 1)[-1], flush=True)
                time.sleep(8 * (attempt + 1))

    publisher.run = run
    publisher.publication_lock = publication_lock
    publisher.api = api
    publisher.prepare_checkout = prepare_checkout
    publisher.public_check = public_check
    original_commit_files = publisher.commit_files

    def commit_files(work, files, message, *, mutable=False):
        print(message, flush=True)
        return original_commit_files(work, files, message, mutable=mutable)

    publisher.commit_files = commit_files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--dotnet', type=Path, default=publisher.ROOT.parent / '.tools/dotnet/dotnet.exe')
    parser.add_argument('--release-root', type=Path, help='Clean, verified release tree; never point this at a running app data directory')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--check', action='store_true')
    mode.add_argument('--publish', action='store_true')
    args = parser.parse_args()
    if args.release_root:
        publisher.ROOT = args.release_root.resolve()
        publisher.CS = publisher.ROOT / 'csharp'
    if args.publish:
        adapt(args.dotnet)
        publisher.run([str(args.dotnet.resolve()), 'restore', str(publisher.CS / 'scripts/VerifyChannel/VerifyChannel.csproj'), '--locked-mode'], cwd=publisher.CS)
        print('Checking frozen signed release and publishing C# ' + args.version, flush=True)
        result = publisher.publish(args.version)
    else:
        result = publisher.checked(args.version)[2]
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
