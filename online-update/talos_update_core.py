"""Signed, explicitly invoked updates; normal startup and game use remain offline."""
from __future__ import annotations
import base64
import binascii
import contextlib
import hashlib
import io
import json
import os
import re
import shutil
import ssl
import stat
import subprocess
import unicodedata
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime
from pathlib import Path, PurePosixPath

LAUNCHER_VERSION = '1.0.0'
MAX_FEED = 1024 * 1024
MAX_PACKAGE = 64 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
MAX_FILE = 32 * 1024 * 1024
MAX_FILES = 256
BASE_DEPENDENCIES = {
    'english-source/manifest.json': (1897, '7db72c8e87418ffdc6c2bce54218429158c0ff8e4f3356116b227829d901204f'),
    'english-source/js/engine.js': (120910, 'bb6a73e4cbc24cfd9c020273c18699fe43ab5bc3f2a87e1a209b038a6f5cf139'),
    'english-source/js/solver.js': (81863, '72f0d01aeee7b9d897a7e742ab2b1b739b6d9e3f5f4104372d2188a8ae3954d5'),
    'english-source/js/tool-combos.js': (5175, '3cc44fbbbc301832ba93292248741879ae9b7a96b21d8b7c53b0fdf25932a51e'),
    'training/english/policy.json': (1360, '471ad7862020d1bc444114a811c087f1c5db2872e350590ab6383b9fadf7e78f'),
}
VERIFY_JS = """'use strict';const fs=require('node:fs'),crypto=require('node:crypto');try{const q=JSON.parse(fs.readFileSync(0,'utf8'));const key=crypto.createPublicKey(q.key);if(key.asymmetricKeyType!=='ed25519')process.exit(3);const ok=crypto.verify(null,Buffer.from(q.payload,'base64'),key,Buffer.from(q.signature,'base64'));process.stdout.write(ok?'verified':'invalid');process.exitCode=ok?0:2;}catch(e){process.exitCode=3;}"""

class UpdateError(Exception):
    code = 'update_error'

class AuthenticationError(UpdateError):
    code = 'authentication'

class ValidationError(UpdateError):
    code = 'validation'

class NetworkError(UpdateError):
    code = 'network'

class UpdateBusy(UpdateError):
    code = 'busy'

class UpdateCancelled(UpdateError):
    code = 'cancelled'

class InstallError(UpdateError):
    code = 'install'


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'), allow_nan=False).encode('utf8')


def _object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValidationError('JSON中存在重复字段，已拒绝更新。')
        value[key] = item
    return value


def _json(data):
    try:
        return json.loads(data.decode('utf8'), object_pairs_hook=_object,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValidationError('JSON含非法数字。')))
    except UpdateError:
        raise
    except (ValueError, UnicodeError, TypeError, RecursionError) as exc:
        raise ValidationError('更新信息不是有效的UTF-8 JSON。') from exc


def _integer(value, low, high, label):
    if type(value) is not int or not low <= value <= high:
        raise ValidationError(f'{label}超出允许范围。')
    return value


def _version(value):
    if not isinstance(value, str) or not re.fullmatch(r'(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})', value):
        raise ValidationError('版本号格式无效。')
    return tuple(map(int, value.split('.')))


def _url(value):
    if not isinstance(value, str) or len(value) > 4096 or any(ord(c) < 33 for c in value):
        raise ValidationError('更新地址格式无效。')
    try:
        parsed = urllib.parse.urlsplit(value)
        if parsed.scheme != 'https' or not parsed.hostname or parsed.username is not None or parsed.password is not None or parsed.fragment or parsed.port not in (None, 443):
            raise ValueError()
    except ValueError as exc:
        raise ValidationError('更新仅接受无凭据、正常HTTPS地址。') from exc
    return value


def _safe_name(value):
    if not isinstance(value, str) or not value or len(value) > 240 or value != unicodedata.normalize('NFC', value):
        raise ValidationError('包内文件名无效或过长。')
    if '\\' in value or value.startswith('/') or any(ord(c) < 32 for c in value) or any(c in value for c in ':<>"|?*'):
        raise ValidationError('包内路径含Windows不安全字符。')
    parts = value.split('/')
    for part in parts:
        if part in ('', '.', '..') or part.endswith(('.', ' ')):
            raise ValidationError('包内路径存在越界或歧义。')
        stem = part.split('.')[0].rstrip(' ').upper()
        if stem in ('CON', 'PRN', 'AUX', 'NUL', 'CLOCK$', 'CONIN$', 'CONOUT$') or re.fullmatch(r'(COM|LPT)[1-9¹²³]', stem):
            raise ValidationError('包内路径使用Windows保留名。')
    return value


def _is_link(path):
    try:
        data = path.lstat()
        return stat.S_ISLNK(data.st_mode) or bool(getattr(data, 'st_file_attributes', 0) & 0x400)
    except FileNotFoundError:
        return False


class _Redirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        _url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


class Updater:
    def __init__(self, install_root, public_key_pem, feed_url='', node_path=None, transport=None):
        self.root = Path(install_root).resolve()
        if not self.root.is_dir():
            raise InstallError('原助手目录不存在，请把启动器放在完整原目录。')
        self.public_key = public_key_pem.decode('ascii') if isinstance(public_key_pem, bytes) else public_key_pem
        if not isinstance(self.public_key, str) or len(self.public_key) > 16384:
            raise AuthenticationError('发布公钥无效。')
        self.feed_url = feed_url
        self._node_override = node_path is not None
        self.node = Path(node_path).resolve() if node_path is not None else self.root / 'runtime' / 'node.exe'
        self.transport = transport
        self._verified = {}
        self._unavailable = []

    def _path(self, relative):
        _safe_name(relative)
        result = self.root
        for part in relative.split('/'):
            result = result / part
            if _is_link(result):
                raise ValidationError('安装路径包含符号链接或重解析点，已拒绝写入或启动。')
        return result

    @contextlib.contextmanager
    def _lock(self):
        # The inode persists: removing it after unlock can let a contender lock an
        # unlinked inode while another process creates a new one. OS locks release
        # automatically on crash, so a stale directory never blocks normal startup.
        lock = self._path('.talos-update-lock')
        try:
            try:
                descriptor = os.open(lock, os.O_RDWR | os.O_CREAT | os.O_EXCL, 0o600)
                os.write(descriptor, b'0'); os.lseek(descriptor, 0, os.SEEK_SET)
            except FileExistsError:
                if not lock.is_file() or _is_link(lock):
                    raise UpdateBusy('更新锁路径不是普通文件，请保留该文件并检查。')
                descriptor = os.open(lock, os.O_RDWR)
        except UpdateError:
            raise
        except OSError as exc:
            raise UpdateBusy('无法打开更新锁，请检查助手目录权限。') from exc
        locked = False
        try:
            try:
                if os.name == 'nt':
                    import msvcrt
                    os.lseek(descriptor, 0, os.SEEK_SET)
                    msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
                locked = True
            except OSError as exc:
                raise UpdateBusy('另一项更新或启动操作正在运行，请稍后重试。') from exc
            yield
        finally:
            if locked:
                try:
                    if os.name == 'nt':
                        import msvcrt
                        os.lseek(descriptor, 0, os.SEEK_SET)
                        msvcrt.locking(descriptor, msvcrt.LK_UNLCK, 1)
                    else:
                        import fcntl
                        fcntl.flock(descriptor, fcntl.LOCK_UN)
                except OSError:
                    pass
            os.close(descriptor)

    def _progress(self, callback, stage, done=0, total=0, message=''):
        if callback is not None and callback({'stage': stage, 'done': done, 'total': total, 'message': message}) is False:
            raise UpdateCancelled('更新已取消，原版本继续保留。')

    def _fetch(self, url, maximum, callback=None):
        _url(url)
        if self.transport is not None:
            try:
                value = self.transport(url, maximum)
            except UpdateError:
                raise
            except Exception as exc:
                raise NetworkError('更新下载失败，请稍后重试。') from exc
            if not isinstance(value, bytes) or len(value) > maximum:
                raise NetworkError('更新下载超出大小上限或返回格式不正确。')
            self._progress(callback, 'download', len(value), len(value), '下载完成，正在验证')
            return value
        try:
            opener = urllib.request.build_opener(_Redirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
            request = urllib.request.Request(url, headers={'User-Agent': 'TalosHelper-Updater/1.0', 'Accept-Encoding': 'identity'})
            with opener.open(request, timeout=15) as response:
                _url(response.geturl())
                size = response.headers.get('Content-Length')
                if size is not None and (not size.isdigit() or int(size) > maximum):
                    raise NetworkError('服务器返回的更新文件过大。')
                total = int(size) if size else 0
                parts = []; received = 0
                while True:
                    chunk = response.read(min(128 * 1024, maximum - received + 1))
                    if not chunk:
                        break
                    received += len(chunk)
                    if received > maximum:
                        raise NetworkError('更新下载超出大小上限。')
                    parts.append(chunk)
                    self._progress(callback, 'download', received, total, '正在下载更新')
                if total and received != total:
                    raise NetworkError('更新下载不完整，请重试。')
                return b''.join(parts)
        except UpdateError:
            raise
        except Exception as exc:
            raise NetworkError('HTTPS更新下载失败，请检查网络和系统时间。') from exc

    def _signature(self, envelope):
        if not isinstance(envelope, dict) or set(envelope) != {'schema', 'payload', 'signature'} or envelope['schema'] != 'talos-signed-feed-v1':
            raise AuthenticationError('更新签名信封格式无效。')
        try:
            payload = base64.b64decode(envelope['payload'], validate=True)
            signature = base64.b64decode(envelope['signature'], validate=True)
            if len(payload) > MAX_FEED // 2 or len(signature) != 64:
                raise ValueError()
        except (ValueError, TypeError, binascii.Error) as exc:
            raise AuthenticationError('更新签名编码无效。') from exc
        if not self._node_override:
            self._path('runtime/node.exe')
        if not self.node.is_file() or _is_link(self.node):
            # Explicit development node_path can be a resolved symlink to the system Node.
            raise AuthenticationError('未找到原助手自带Node，无法验证发布签名。')
        environment = os.environ.copy()
        environment.pop('NODE_OPTIONS', None); environment.pop('NODE_PATH', None)
        request = {'key': self.public_key, 'payload': envelope['payload'], 'signature': envelope['signature']}
        try:
            answer = subprocess.run([str(self.node), '--eval', VERIFY_JS], input=_canonical(request), stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, shell=False, timeout=10, env=environment,
                                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        except (OSError, subprocess.SubprocessError) as exc:
            raise AuthenticationError('本地签名验证未能完成，已停止更新。') from exc
        if answer.returncode != 0 or answer.stdout != b'verified':
            raise AuthenticationError('更新发布签名无效，已拒绝安装。')
        return payload

    def _manifest(self, envelope):
        payload = self._signature(envelope)
        data = _json(payload)
        fields = {'schema', 'app', 'version', 'launcher_min', 'base_version', 'published_at', 'notes', 'entry', 'package', 'files'}
        if not isinstance(data, dict) or set(data) != fields or data['schema'] != 'talos-release-v1' or data['app'] != 'TalosHelper':
            raise ValidationError('更新清单格式不受支持。')
        _version(data['version'])
        if _version(data['launcher_min']) > _version(LAUNCHER_VERSION) or data['base_version'] != '0.3':
            raise ValidationError('此更新需要其他启动器或基础版本，当前不能安装。')
        if not isinstance(data['notes'], list) or len(data['notes']) > 30 or any(not isinstance(s, str) or len(s) > 4000 for s in data['notes']):
            raise ValidationError('更新说明格式无效。')
        try:
            if not isinstance(data['published_at'], str) or len(data['published_at']) > 50 or datetime.fromisoformat(data['published_at'].replace('Z', '+00:00')).tzinfo is None:
                raise ValueError()
        except (ValueError, TypeError) as exc:
            raise ValidationError('更新发布日期格式无效。') from exc
        entry = f"TalosHelper-App-{data['version']}.exe"
        if data['entry'] != entry:
            raise ValidationError('更新启动文件不符合固定版本路径。')
        package = data['package']
        if not isinstance(package, dict) or set(package) != {'url', 'bytes', 'sha256'}:
            raise ValidationError('更新包信息格式无效。')
        _url(package['url']); _integer(package['bytes'], 1, MAX_PACKAGE, '更新包大小')
        self._hash_text(package['sha256'])
        files = data['files']
        if not isinstance(files, list) or not 1 <= len(files) <= MAX_FILES:
            raise ValidationError('更新文件数量无效。')
        names = set(); total = 0; prefix = f"releases/{data['version']}/"
        for item in files:
            if not isinstance(item, dict) or set(item) != {'path', 'bytes', 'sha256'}:
                raise ValidationError('更新文件条目格式无效。')
            name = _safe_name(item['path'])
            if name != entry and not name.startswith(prefix):
                raise ValidationError('更新包试图修改共享运行环境或其他版本。')
            relative = name[len(prefix):] if name.startswith(prefix) else None
            if relative and (relative.casefold() == 'english-source' or relative.casefold().startswith('english-source/') or relative.casefold() == 'training/english' or relative.casefold().startswith('training/english/')):
                raise ValidationError('更新包不能分发原模拟器源码或历史策略。')
            folded = name.casefold()
            if folded in names or any(folded.startswith(other + '/') or other.startswith(folded + '/') for other in names):
                raise ValidationError('更新文件存在大小写冲突、重复或文件目录冲突。')
            names.add(folded)
            total += _integer(item['bytes'], 0, MAX_FILE, '单个更新文件大小'); self._hash_text(item['sha256'])
        if entry.casefold() not in names or total > MAX_EXPANDED:
            raise ValidationError('更新缺少启动文件或解压大小过大。')
        return data

    @staticmethod
    def _hash_text(value):
        if not isinstance(value, str) or not re.fullmatch('[a-f0-9]{64}', value):
            raise ValidationError('更新文件SHA-256无效。')

    def check(self):
        if not self.feed_url:
            raise NetworkError('尚未配置公开发布地址；检查更新暂不可用，可使用本地已签名更新包。')
        envelope = _json(self._fetch(self.feed_url, MAX_FEED))
        manifest = self._manifest(envelope)
        self._verified[_canonical(manifest)] = json.loads(json.dumps(envelope))
        return json.loads(json.dumps(manifest))

    def _authenticated(self, manifest):
        try:
            envelope = self._verified.get(_canonical(manifest))
        except (ValueError, TypeError, RecursionError) as exc:
            raise AuthenticationError('安装信息没有经过当前启动器验签。') from exc
        if envelope is None:
            raise AuthenticationError('安装清单未验签或已被修改，请重新检查更新。')
        verified = self._manifest(envelope)
        if _canonical(verified) != _canonical(manifest):
            raise AuthenticationError('验签后的更新信息已改变。')
        return verified, envelope

    def install(self, manifest, progress=None):
        manifest, envelope = self._authenticated(manifest)
        with self._lock():
            data = self._fetch(manifest['package']['url'], manifest['package']['bytes'], progress)
            return self._install(manifest, envelope, data, progress)

    def install_local(self, feed_path, zip_path):
        envelope = _json(self._read_bounded(Path(feed_path), MAX_FEED))
        manifest = self._manifest(envelope)
        with self._lock():
            data = self._read_bounded(Path(zip_path), MAX_PACKAGE)
            return self._install(manifest, envelope, data, None)

    @staticmethod
    def _read_bounded(file, maximum):
        try:
            with file.open('rb') as handle:
                data = handle.read(maximum + 1)
            if len(data) > maximum:
                raise ValidationError('本地更新文件过大。')
            return data
        except UpdateError:
            raise
        except OSError as exc:
            raise InstallError('无法读取本地更新文件。') from exc

    def _verify_file(self, file, size, digest):
        if _is_link(file) or not file.is_file():
            raise ValidationError(f'已安装文件缺失或不是普通文件：{file.name}')
        try:
            if file.stat().st_size != size:
                raise ValidationError(f'文件大小校验失败：{file.name}')
            with file.open('rb') as handle:
                actual = hashlib.file_digest(handle, 'sha256').hexdigest() if hasattr(hashlib, 'file_digest') else self._stream_hash(handle)
            if actual != digest:
                raise ValidationError(f'文件SHA-256校验失败：{file.name}')
        except OSError as exc:
            raise ValidationError(f'无法校验本地文件：{file.name}') from exc

    @staticmethod
    def _stream_hash(handle):
        value = hashlib.sha256()
        for chunk in iter(lambda: handle.read(128 * 1024), b''):
            value.update(chunk)
        return value.hexdigest()

    def _release_files(self, manifest):
        prefix = f"releases/{manifest['version']}/"
        expected = {item['path']: (item['bytes'], item['sha256']) for item in manifest['files']}
        expected.update({prefix + name: spec for name, spec in BASE_DEPENDENCIES.items()})
        return expected

    def _validate_installed(self, manifest):
        expected = self._release_files(manifest)
        for name, (size, digest) in expected.items():
            self._verify_file(self._path(name), size, digest)
        directory = self._path(f"releases/{manifest['version']}")
        if not directory.is_dir():
            raise ValidationError('已安装版本目录缺失。')
        # Additional files can alter Python/Node module resolution; release directories are immutable.
        for walk_root, directories, files in os.walk(directory, followlinks=False):
            for name in directories + files:
                candidate = Path(walk_root) / name
                if _is_link(candidate):
                    raise ValidationError('版本目录含符号链接或重解析点。')
            for name in files:
                relative = (Path(walk_root) / name).relative_to(self.root).as_posix()
                if relative not in expected:
                    raise ValidationError('已安装版本中存在未列入签名清单的文件，已拒绝启动。')

    def _state(self, allow_broken_active=False):
        file = self._path('talos-update-state.json')
        self._unavailable = []
        if not file.exists():
            return {'schema': 'talos-update-state-v1', 'active': None, 'history': [], 'installed': {}}, {}
        state = _json(self._read_bounded(file, MAX_FEED * 8))
        if not isinstance(state, dict) or set(state) != {'schema', 'active', 'history', 'installed'} or state['schema'] != 'talos-update-state-v1' or not isinstance(state['installed'], dict) or not isinstance(state['history'], list):
            raise ValidationError('本地更新状态损坏，请保留文件并重新导入已签名更新包。')
        if len(state['installed']) > 100 or len(state['history']) > 100 or any(not isinstance(v, str) for v in state['history']) or (state['active'] is not None and not isinstance(state['active'], str)) or len(set(state['history'])) != len(state['history']):
            raise ValidationError('本地版本历史格式无效。')
        manifests = {}
        for version, record in state['installed'].items():
            _version(version)
            try:
                if not isinstance(record, dict) or set(record) != {'feed'}:
                    raise ValidationError('本地版本身份记录无效。')
                manifest = self._manifest(record['feed'])
                if manifest['version'] != version:
                    raise ValidationError('本地版本记录与发布签名不一致。')
                self._validate_installed(manifest); manifests[version] = manifest
            except UpdateError as exc:
                if version == state['active'] and not allow_broken_active:
                    raise
                # A damaged older release cannot prevent the current verified
                # release from launching. It remains unavailable for rollback.
                self._unavailable.append({'version': version, 'error': str(exc)})
        if state['active'] not in state['installed'] or (not allow_broken_active and state['active'] not in manifests) or any(version not in state['installed'] for version in state['history']):
            raise ValidationError('本地活动版本或历史记录无效。')
        return state, manifests

    @staticmethod
    def _info(manifest):
        return {name: manifest[name] for name in ('version', 'entry', 'notes', 'published_at')}

    def _status_value(self, state, manifests):
        return {'active': state['active'], 'active_valid': state['active'] in manifests, 'history': list(state['history']), 'installed': {v: self._info(m) for v, m in manifests.items()}, 'unavailable': list(self._unavailable)}

    def status(self):
        if not self._path('talos-update-state.json').exists():
            bootstrap = self._path('talos-updater/bootstrap/latest.json')
            package = self._path('talos-updater/bootstrap/package.zip')
            if bootstrap.exists() and package.exists():
                self.install_local(bootstrap, package)
        state, manifests = self._state(allow_broken_active=True)
        return self._status_value(state, manifests)

    def _mkdirs(self, directory, created):
        if directory == self.root:
            return
        missing = []
        current = directory
        while current != self.root and not current.exists():
            missing.append(current); current = current.parent
        self._path(directory.relative_to(self.root).as_posix())
        for current in reversed(missing):
            current.mkdir(); created.append(current)

    def _publish(self, source, target, created):
        self._mkdirs(target.parent, created)
        # Publish an already complete staged inode, never copy into the final
        # executable name. A hard process termination cannot leave a truncated
        # final file. link() also refuses to overwrite an existing user file.
        with source.open('r+b') as content:
            os.fsync(content.fileno())
        try:
            os.link(source, target)
        except OSError:
            if os.name != 'nt' or target.exists():
                raise
            # Windows rename on the same volume is atomic and never replaces an
            # existing target; this also supports filesystems without hard links.
            os.rename(source, target)
        created.append(target)
        self._sync_dir(target.parent)

    @staticmethod
    def _sync_dir(directory):
        # Windows may not allow opening directories as file descriptors. File
        # contents are still fsynced; unsupported directory flushes are best effort.
        try:
            descriptor = os.open(directory, os.O_RDONLY | getattr(os, 'O_DIRECTORY', 0))
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        except OSError:
            pass

    def _atomic_state(self, state):
        target = self._path('talos-update-state.json')
        temp = self._path(f'.talos-state-{os.urandom(16).hex()}')
        try:
            with temp.open('xb') as handle:
                handle.write(_canonical(state)); handle.flush(); os.fsync(handle.fileno())
            os.replace(temp, target)
            self._sync_dir(self.root)
        finally:
            if temp.exists():
                temp.unlink()

    def _extract(self, manifest, data, directory, progress):
        expected = {item['path']: item for item in manifest['files']}
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                infos = archive.infolist()
                if len(infos) != len(expected) or len(infos) > MAX_FILES:
                    raise ValidationError('更新ZIP文件列表与签名清单不一致。')
                seen = set(); expanded = 0
                for index, item in enumerate(infos):
                    name = _safe_name(item.filename)
                    folded = name.casefold()
                    mode = (item.external_attr >> 16) & 0xffff
                    if name not in expected or folded in seen or item.is_dir() or item.external_attr & 0x10 or item.flag_bits & 1 or stat.S_ISLNK(mode) or stat.S_IFMT(mode) not in (0, stat.S_IFREG) or item.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                        raise ValidationError('ZIP含未声明、重复、加密、链接或特殊文件。')
                    if item.orig_filename != item.filename:
                        raise ValidationError('ZIP文件名含零字节或编码歧义。')
                    spec = expected[name]
                    if item.file_size != spec['bytes'] or item.file_size > MAX_FILE or item.file_size > max(1024 * 1024, item.compress_size * 200):
                        raise ValidationError('ZIP解压大小或压缩比不安全。')
                    expanded += item.file_size
                    if expanded > MAX_EXPANDED:
                        raise ValidationError('ZIP解压总大小超限。')
                    seen.add(folded)
                    target = directory / PurePosixPath(name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    digest = hashlib.sha256(); count = 0
                    with archive.open(item) as source, target.open('xb') as output:
                        while True:
                            chunk = source.read(128 * 1024)
                            if not chunk:
                                break
                            count += len(chunk)
                            if count > spec['bytes']:
                                raise ValidationError('ZIP成员实际大小超出签名清单。')
                            digest.update(chunk); output.write(chunk)
                    if count != spec['bytes'] or digest.hexdigest() != spec['sha256']:
                        raise ValidationError('ZIP成员内容与签名SHA-256不一致。')
                    self._progress(progress, 'extract', index + 1, len(infos), '正在校验并展开更新')
        except UpdateError:
            raise
        except (OSError, ValueError, zipfile.BadZipFile, RuntimeError, EOFError) as exc:
            raise ValidationError('更新ZIP损坏或无法安全解压。') from exc

    def _install(self, manifest, envelope, data, progress):
        if len(data) != manifest['package']['bytes'] or _sha(data) != manifest['package']['sha256']:
            raise ValidationError('更新包字节数或SHA-256不匹配，原版本保持不变。')
        self._progress(progress, 'verify', len(data), len(data), '更新包签名及SHA-256已核对')
        state, manifests = self._state(allow_broken_active=True)
        version = manifest['version']; created = []; commit_attempted = False
        if state['active'] and _version(version) < _version(state['active']):
            raise ValidationError('不能通过安装接口降级；请使用已验证本地版本的回退功能。')
        stage = self._path(f'.talos-stage-{os.urandom(16).hex()}'); stage.mkdir()
        try:
            self._extract(manifest, data, stage, progress)
            prefix = f'releases/{version}/'
            for index, (name, (size, digest)) in enumerate(BASE_DEPENDENCIES.items()):
                source = self._path(name); self._verify_file(source, size, digest)
                target = stage / prefix / name; target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target); self._verify_file(target, size, digest)
                self._progress(progress, 'dependencies', index + 1, len(BASE_DEPENDENCIES), '正在核对本机原运行依赖')
            release = self._path(f'releases/{version}'); entry = self._path(manifest['entry'])
            if release.exists() and version in state['installed']:
                self._validate_installed(manifest)
            if entry.exists():
                # A matching orphan EXE can be retained, but is never overwritten.
                item = next(f for f in manifest['files'] if f['path'] == manifest['entry'])
                self._verify_file(entry, item['bytes'], item['sha256'])
            if version in manifests and _canonical(manifests[version]) != _canonical(manifest):
                raise ValidationError('同一版本已有不同的签名身份，不能覆盖安装。')
            expected = self._release_files(manifest)
            for name, (size, digest) in expected.items():
                target = self._path(name)
                if target.exists():
                    self._verify_file(target, size, digest)
                else:
                    self._publish(stage / PurePosixPath(name), target, created)
            self._validate_installed(manifest)
            old_active = state['active']
            history = [v for v in state['history'] if v != version]
            if old_active and old_active != version:
                history = [old_active] + [v for v in history if v != old_active]
            state['installed'][version] = {'feed': envelope}
            state['active'] = version; state['history'] = history
            self._progress(progress, 'activate', 1, 1, '正在原子切换活动版本')
            commit_attempted = True
            self._atomic_state(state)
            return {**self._info(manifest), 'active': version, 'history': history}
        except Exception as exc:
            # A filesystem wrapper can report failure after rename committed.
            # Never delete a version which is already the durable active state.
            if commit_attempted:
                try:
                    if self._read_bounded(self._path('talos-update-state.json'), MAX_FEED * 8) == _canonical(state):
                        self._validate_installed(manifest)
                        return {**self._info(manifest), 'active': version, 'history': state['history']}
                except UpdateError:
                    pass
            for target in reversed(created):
                try:
                    if target.is_dir() and not _is_link(target):
                        target.rmdir()
                    else:
                        target.unlink()
                except OSError:
                    pass
            if isinstance(exc, UpdateError):
                raise
            raise InstallError('安装未完成，活动版本保持不变，请检查磁盘空间或文件占用。') from exc
        finally:
            shutil.rmtree(stage, ignore_errors=True)

    def rollback(self, version=None):
        with self._lock():
            state, manifests = self._state(allow_broken_active=True)
            chosen = version if version is not None else next((v for v in state['history'] if v != state['active'] and v in manifests), None)
            if chosen not in manifests or chosen == state['active']:
                raise ValidationError('没有可回退的完整本地版本。')
            old = state['active']; state['active'] = chosen
            state['history'] = [old] + [v for v in state['history'] if v not in (chosen, old)]
            self._atomic_state(state)
            return {**self._info(manifests[chosen]), 'active': chosen, 'history': state['history']}

    def launch_active(self):
        with self._lock():
            state, manifests = self._state()
            if state['active'] is None:
                raise ValidationError('尚无可启动的已验证版本。')
            manifest = manifests[state['active']]
            executable = self._path(manifest['entry'])
            try:
                process = subprocess.Popen([str(executable)], cwd=str(self.root), shell=False, close_fds=True)
            except OSError as exc:
                raise InstallError('已验证版本未能启动，请确认在Windows完整助手目录中运行。') from exc
            return {'version': state['active'], 'pid': process.pid, 'entry': manifest['entry']}
