#!/usr/bin/env python3
"""Build isolated Talos applications and a pinned update launcher.

Only the original CArchive 'app' entry changes. No original application code or
Windows executable is executed by the builder or its synthetic-loader tests.
Signing feeds and assembling distribution ZIPs are separate release actions.
"""
from __future__ import annotations

import argparse
import ast
import datetime
import hashlib
import importlib.util
import json
import marshal
from pathlib import Path
import re
import struct
import sys
import tempfile
import types

ROOT = Path(__file__).resolve().parent.parent
LAUNCHER_NAME = 'TalosHelper-启动器.exe'
LAUNCHER_VERSION = '1.0.0'
DEFAULT_FEED_URL = 'https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json'
DEFAULT_ORIGINAL = Path('/workspace/legacy-talos-v03/TalosHelper.exe')
DEFAULT_BASE_MANIFEST = Path('/workspace/attachments/f091bdef-c01d-4ee5-b5af-bcd6f90a64b9/package-manifest.json')
BACKEND_FILES = ('game-runtime.cjs', 'game-advice.cjs', 'game-metadata.json', 'game-validation.json', 'game-validation.md', 'game-validation.rows.csv.gz')
# PyInstaller's bootstrap base_library.zip roots and CPython Windows builtins.
# Do not allow arbitrary members of the development machine's stdlib: uuid, for
# example, exists on Linux but is absent from this frozen Windows distribution.
BASE_LIBRARY_ROOTS = {'_collections_abc', '_weakrefset', 'abc', 'codecs', 'collections', 'copyreg', 'encodings', 'enum', 'functools', 'genericpath', 'io', 'keyword', 'linecache', 'locale', 'ntpath', 'operator', 'os', 'posixpath', 're', 'reprlib', 'stat', 'traceback', 'types', 'warnings', 'weakref'}
WINDOWS_BUILTIN_ROOTS = {'_abc', '_ast', '_codecs', '_collections', '_functools', '_imp', '_io', '_operator', '_signal', '_sre', '_stat', '_string', '_thread', '_warnings', '_weakref', '_winapi', 'binascii', 'builtins', 'errno', 'itertools', 'marshal', 'msvcrt', 'nt', 'sys', 'time', 'winreg', 'zlib'}


def _legacy():
    name = '_talos_legacy_archive_builder'
    if name not in sys.modules:
        spec = importlib.util.spec_from_file_location(name, ROOT / 'legacy-update' / 'build-update.py')
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module
        try:
            spec.loader.exec_module(module)
        except BaseException:
            sys.modules.pop(name, None)
            raise
    return sys.modules[name]


def sha256(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def _require(condition, message):
    if not condition:
        raise ValueError(message)


def _version(version):
    _require(isinstance(version, str) and re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version), 'Version must be a canonical numeric major.minor.patch')
    return version


def _hash(value):
    _require(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value), 'Expected a lowercase SHA256')
    return value


def _public_key(public_key_pem):
    _require(isinstance(public_key_pem, str) and '-----BEGIN PUBLIC KEY-----' in public_key_pem and '-----END PUBLIC KEY-----' in public_key_pem and 'PRIVATE' not in public_key_pem and len(public_key_pem) < 4096, 'Only a public PEM key may enter the launcher')
    return public_key_pem


def app_loader_code(version: str, helper_sha256: str) -> types.CodeType:
    _version(version)
    _hash(helper_sha256)
    source = '''def calculate(self, keep_status=False):
    try:
        import hashlib
        import importlib.util
        from pathlib import Path
        import sys
        sys.dont_write_bytecode = True
        expected = __PIN__
        version = __VERSION__
        base_root = Path(sys.executable).resolve().parent if getattr(sys, 'frozen', False) else Path(ROOT).resolve()
        release_root = (base_root / 'releases' / version).resolve()
        if not release_root.is_relative_to(base_root):
            raise RuntimeError('版本目录超出原助手目录，已停止加载。')
        self._talos_install_root = release_root
        self._talos_base_root = base_root
        helper = release_root / 'talos_game_update.py'
        module_name = '_talos_release_' + version.replace('.', '_') + '_' + expected[:16]
        module = sys.modules.get(module_name)
        if module is None:
            content = helper.read_bytes()
            if hashlib.sha256(content).hexdigest() != expected:
                raise RuntimeError('版本组件校验失败，请通过启动器重新安装该版本。')
            spec = importlib.util.spec_from_file_location(module_name, str(helper))
            if spec is None or spec.loader is None or not hasattr(spec.loader, 'source_to_code'):
                raise RuntimeError('无法读取版本组件。')
            module = importlib.util.module_from_spec(spec)
            code = spec.loader.source_to_code(content, str(helper))
            sys.modules[module_name] = module
            try:
                exec(code, module.__dict__)
                if not callable(getattr(module, 'calculate', None)):
                    raise RuntimeError('版本组件缺少计算接口。')
                module.__talos_verified_sha256__ = expected
            except BaseException:
                sys.modules.pop(module_name, None)
                raise
        elif getattr(module, '__talos_verified_sha256__', None) != expected:
            raise RuntimeError('版本组件缓存校验失败，请重启助手。')
        return module.calculate(self, keep_status=keep_status)
    except Exception as error:
        message = '截图策略组件加载失败：' + str(error)
        self.status.set(message)
        messagebox.showerror('版本组件', message)
'''.replace('__PIN__', repr(helper_sha256)).replace('__VERSION__', repr(version))
    root = compile(source, 'talos_isolated_app_loader.py', 'exec', dont_inherit=True)
    code = next(value for value in root.co_consts if isinstance(value, types.CodeType) and value.co_name == 'calculate')
    _require(code.co_argcount == 2 and code.co_varnames[:2] == ('self', 'keep_status') and not code.co_freevars, 'Unexpected App.calculate loader signature')
    return code.replace(co_qualname='App.calculate')


def launcher_code(core_sha256: str, ui_sha256: str, public_key_pem: str, feed_url: str = '') -> types.CodeType:
    pins = {'talos_update_core': _hash(core_sha256), 'launcher_ui': _hash(ui_sha256)}
    _public_key(public_key_pem)
    _require(isinstance(feed_url, str) and len(feed_url) <= 4096, 'Feed URL must be a bounded string')
    source = '''"""Pinned Talos update launcher. Startup performs no online check."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
sys.dont_write_bytecode = True

EXPECTED_MODULE_SHA256 = __PINS__
PUBLIC_KEY_PEM = __KEY__
DEFAULT_FEED_URL = __FEED__
LAUNCHER_VERSION = '1.0.0'

def _install_root():
    return Path(sys.executable).resolve().parent if getattr(sys, 'frozen', False) else Path(__file__).resolve().parent

def _load_modules(root):
    folder = (root / 'talos-updater').resolve()
    if not folder.is_relative_to(root):
        raise RuntimeError('更新组件目录超出安装目录。')
    verified = {}
    # Verify both modules before executing either; compile these exact bytes.
    for name, expected in EXPECTED_MODULE_SHA256.items():
        path = folder / (name + '.py')
        content = path.read_bytes()
        if len(content) > 2 * 1024 * 1024 or hashlib.sha256(content).hexdigest() != expected:
            raise RuntimeError('更新组件校验失败：' + name + '。请重新解压首次启动器包。')
        verified[name] = (path, content)
    loaded = {}
    for name in ('talos_update_core', 'launcher_ui'):
        path, content = verified[name]
        expected = EXPECTED_MODULE_SHA256[name]
        cached = sys.modules.get(name)
        if cached is not None and getattr(cached, '__talos_verified_sha256__', None) == expected:
            loaded[name] = cached
            continue
        spec = importlib.util.spec_from_file_location(name, str(path))
        if spec is None or spec.loader is None or not hasattr(spec.loader, 'source_to_code'):
            raise RuntimeError('无法读取更新组件：' + name)
        module = importlib.util.module_from_spec(spec)
        code = spec.loader.source_to_code(content, str(path))
        previous = sys.modules.get(name)
        sys.modules[name] = module
        try:
            exec(code, module.__dict__)
            module.__talos_verified_sha256__ = expected
        except BaseException:
            if previous is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = previous
            raise
        loaded[name] = module
    return loaded

def _unique_json(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('配置存在重复字段。')
        result[key] = value
    return result

def _config(root):
    path = root / 'talos-updater' / 'config.json'
    if not path.exists():
        return {'schema': 'talos-updater-config-v1', 'feed_url': DEFAULT_FEED_URL}
    content = path.read_bytes()
    if len(content) > 16384:
        raise ValueError('启动器配置过大。')
    config = json.loads(content.decode('utf-8'), object_pairs_hook=_unique_json)
    if not isinstance(config, dict) or config.get('schema') != 'talos-updater-config-v1' or any(key not in ('schema', 'feed_url') for key in config):
        raise ValueError('启动器配置格式无效。')
    if not isinstance(config.get('feed_url'), str) or len(config['feed_url']) > 4096:
        raise ValueError('更新地址格式无效。')
    return config

def main():
    root = _install_root()
    modules = _load_modules(root)
    ui = modules['launcher_ui']
    if not callable(getattr(ui, 'run', None)):
        raise RuntimeError('启动器界面缺少run接口。')
    # UI creates the updater in its worker. No startup check/download here.
    result = ui.run(root, PUBLIC_KEY_PEM, config=_config(root))
    return result if isinstance(result, int) else 0

if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        try:
            import tkinter as tk
            from tkinter import messagebox
            window = tk.Tk()
            window.withdraw()
            messagebox.showerror('Talos 启动器', '启动失败：' + str(error))
            window.destroy()
        except Exception:
            pass
        sys.exit(1)
'''.replace('__PINS__', repr(pins)).replace('__KEY__', repr(public_key_pem)).replace('__FEED__', repr(feed_url))
    return compile(source, 'talos_launcher_bootstrap.py', 'exec', dont_inherit=True)


def _archive(original_exe_bytes):
    legacy = _legacy()
    _require(sha256(original_exe_bytes) == legacy.EXPECTED_ORIGINAL_SHA256, 'Original v0.3 executable SHA256 differs')
    archive = legacy.Archive.read(original_exe_bytes)
    _require(archive.entry('app').kind == b's', 'Original archive app entry is not a script')
    return archive


def _rebuild(archive, root_code):
    legacy = _legacy()
    executable = archive.rebuild({'app': marshal.dumps(root_code)})
    rebuilt = legacy.Archive.read(executable)
    _require(rebuilt.prefix == archive.prefix, 'Original PE prefix changed')
    _require([entry.name for entry in archive.entries] == [entry.name for entry in rebuilt.entries], 'Archive members changed')
    unchanged = []
    for original, updated in zip(archive.entries, rebuilt.entries):
        _require((original.kind, original.compressed) == (updated.kind, updated.compressed), 'Archive member type/compression changed')
        _require(original.original_toc[legacy.TOC_HEADER.size:] == updated.original_toc[legacy.TOC_HEADER.size:], 'Archive member name/padding changed')
        if original.name != 'app':
            _require(original.packed == updated.packed and original.unpacked_size == updated.unpacked_size, 'Unexpected compressed member change: ' + original.name)
            unchanged.append({'name': original.name, 'packedSha256': sha256(original.packed)})
    _require(legacy.code_identity(marshal.loads(rebuilt.entry('app').content())) == legacy.code_identity(root_code), 'Rebuilt entry code differs')
    return executable, {
        'originalExeSha256': legacy.EXPECTED_ORIGINAL_SHA256,
        'executableSha256': sha256(executable), 'executableBytes': len(executable),
        'pePrefixUnchanged': True, 'pePrefixSha256': sha256(archive.prefix),
        'pyzBytesUnchanged': True, 'onlyModifiedArchiveEntry': 'app',
        'unchangedCompressedMembers': unchanged,
        'oldEmbeddedCodeExecutedDuringBuild': False,
        'windowsExecutableExecutedDuringBuild': False,
    }


def build_app(original_exe_bytes: bytes, version: str, helper_bytes: bytes):
    version = _version(version)
    archive = _archive(original_exe_bytes)
    legacy = _legacy()
    helper_hash = sha256(helper_bytes)
    loader = app_loader_code(version, helper_hash)
    text = {
        '塔罗斯决策助手 · Demo 0.3 英文规则': '塔罗斯助手 · ' + version + ' 截图策略与更新',
        '本地识别 · 可修正沙盘 · 自动比较连续吞噬': '截图须核对后使用修正规则 · 游戏分析离线运行',
        'Demo 0.3 · 新策略用于英文存档建议；截图模块尚未迁移。': version + ' · 截图须核对后使用修正规则；仅检查和下载更新时联网。',
        '\n\n该建议使用存档本身的练习参数和训练所得权重。左侧是独立的实机截图编辑器；点击“计算下一步”会回到实机策略。': '\n\n英文存档仍使用历史规则与参数；截图须核对后使用修正规则基线。游戏分析离线，更新检查由启动器单独处理。',
    }
    counts = {key: 0 for key in text}
    edits = []

    def visit(code):
        if code.co_qualname == 'App.calculate':
            _require(code.co_argcount == 2 and code.co_varnames[:2] == ('self', 'keep_status') and not code.co_freevars, 'Original calculate signature differs')
            edits.append({'function': code.co_qualname, 'change': 'Version-isolated helper loader', 'release': version, 'helperSha256': helper_hash})
            return loader
        constants = []
        for value in code.co_consts:
            if isinstance(value, types.CodeType):
                constants.append(visit(value))
            elif isinstance(value, str) and value in text:
                counts[value] += 1
                constants.append(text[value])
                edits.append({'function': code.co_qualname, 'before': value, 'after': text[value]})
            else:
                constants.append(value)
        return code.replace(co_consts=tuple(constants))

    original = marshal.loads(archive.entry('app').content())
    root_code = visit(original)
    _require(all(count == 1 for count in counts.values()) and sum(edit.get('function') == 'App.calculate' for edit in edits) == 1, 'Pinned source function/text locations differ')

    def compare(before, after):
        if before.co_qualname == 'App.calculate':
            _require(legacy.code_identity(after) == legacy.code_identity(loader), 'Unexpected calculate loader')
            return
        _require(all(getattr(before, key) == getattr(after, key) for key in legacy.CODE_ATTRIBUTES), 'Unspecified code metadata changed')
        _require(len(before.co_consts) == len(after.co_consts), 'Unspecified constant count change')
        for first, second in zip(before.co_consts, after.co_consts):
            if isinstance(first, types.CodeType):
                compare(first, second)
            else:
                _require(second == (text[first] if isinstance(first, str) and first in text else first), 'Unspecified app constant changed')
    compare(original, root_code)
    executable, audit = _rebuild(archive, root_code)
    audit.update(kind='isolated-app', version=version, entry=f'TalosHelper-App-{version}.exe', helperSha256=helper_hash, releaseDirectory=f'releases/{version}', edits=edits)
    return executable, audit


def build_launcher(original_exe_bytes: bytes, core_bytes: bytes, ui_bytes: bytes, public_key_pem: str, feed_url: str = ''):
    archive = _archive(original_exe_bytes)
    core_hash, ui_hash = sha256(core_bytes), sha256(ui_bytes)
    code = launcher_code(core_hash, ui_hash, public_key_pem, feed_url)
    executable, audit = _rebuild(archive, code)
    audit.update(kind='stable-launcher', version=LAUNCHER_VERSION, entry=LAUNCHER_NAME, coreSha256=core_hash, uiSha256=ui_hash, publicKeySha256=sha256(public_key_pem.encode()), defaultFeedUrl=feed_url, createsUpdaterInUiWorker=True, performsOnlineCheckOnStartup=False)
    return executable, audit


def _file_records(files):
    return [{'path': name, 'bytes': len(content), 'sha256': sha256(content)} for name, content in sorted(files.items())]


def audit_direct_imports(archive, sources: dict[str, bytes], base_manifest_path: Path = DEFAULT_BASE_MANIFEST):
    pyz = archive.entry('PYZ.pyz').content()
    pyz_offset = struct.unpack_from('!I', pyz, 8)[0]
    pyz_modules = set(dict(marshal.loads(pyz[pyz_offset:])))
    manifest_bytes = base_manifest_path.read_bytes()
    base = json.loads(manifest_bytes)
    _require(base.get('schema') == 'talos-share-v1' and base.get('platform') == 'Windows x64', 'Expected the original Windows package manifest')
    paths = {record['file'].replace('\\', '/') for record in base['files']}
    _require('_internal/base_library.zip' in paths, 'Original bootstrap base_library.zip missing from the manifest')
    native = set()
    for path in paths:
        if path.startswith('_internal/') and path.endswith('.pyd'):
            native.add(path[len('_internal/'):-4].split('.cp312-')[0].replace('/', '.'))
    local = {'talos_update_core', 'launcher_ui'}
    result = {}
    skipped = {}

    def windows_condition(node):
        if isinstance(node, ast.Compare) and len(node.ops) == len(node.comparators) == 1:
            left, right = node.left, node.comparators[0]
            if isinstance(left, ast.Attribute) and isinstance(left.value, ast.Name) and isinstance(right, ast.Constant):
                target = {('os', 'name'): 'nt', ('sys', 'platform'): 'win32'}.get((left.value.id, left.attr))
                if target is not None and isinstance(right.value, str):
                    if isinstance(node.ops[0], ast.Eq):
                        return target == right.value
                    if isinstance(node.ops[0], ast.NotEq):
                        return target != right.value
        return None

    for filename, content in sources.items():
        tree = ast.parse(content, filename=filename)
        requested = set()
        skipped[filename] = []

        class Imports(ast.NodeVisitor):
            def visit_If(self, node):
                target = windows_condition(node.test)
                if target is None:
                    self.generic_visit(node)
                else:
                    selected, other = (node.body, node.orelse) if target else (node.orelse, node.body)
                    for statement in other:
                        skipped[filename].extend(item.name for child in ast.walk(statement) if isinstance(child, ast.Import) for item in child.names)
                    for statement in selected:
                        self.visit(statement)

            def visit_Import(self, node):
                requested.update(item.name for item in node.names)

            def visit_ImportFrom(self, node):
                if node.module:
                    _require(node.level == 0, 'Unexpected relative import in standalone update module')
                    requested.add(node.module)

            def visit_Call(self, node):
                if isinstance(node.func, ast.Name) and node.func.id == '__import__':
                    _require(node.args and isinstance(node.args[0], ast.Constant) and isinstance(node.args[0].value, str), 'Dynamic import cannot be checked: ' + filename)
                    requested.add(node.args[0].value)
                self.generic_visit(node)

        Imports().visit(tree)
        proof = {}
        for name in sorted(requested):
            root = name.split('.')[0]
            if name in local:
                proof[name] = 'pinned update module'
            elif name in pyz_modules:
                proof[name] = 'original PYZ module'
            elif name in native:
                proof[name] = 'original manifest .pyd'
            elif root in WINDOWS_BUILTIN_ROOTS:
                proof[name] = 'CPython 3.12 Windows builtin'
            elif root in BASE_LIBRARY_ROOTS:
                proof[name] = 'PyInstaller bootstrap base_library.zip'
            else:
                raise ValueError('Import unavailable in original Windows runtime: ' + filename + ': ' + name)
        result[filename] = proof
    return {'directImportsChecked': True, 'pyzModules': len(pyz_modules), 'baseManifestSha256': sha256(manifest_bytes), 'moduleProofs': result, 'importsExcludedByExplicitNonWindowsBranch': skipped, 'scope': 'Static and literal __import__ statements on Windows paths in the three added Python modules; explicit os.name/sys.platform branches evaluated without running code; does not claim Windows execution.'}


def build_staging(original_path: Path, version: str, public_key_path: Path, feed_url: str, output_dir: Path):
    version = _version(version)
    public_key = _public_key(public_key_path.read_text(encoding='utf-8'))
    original_bytes = original_path.read_bytes()
    folder = Path(__file__).resolve().parent
    core = (folder / 'talos_update_core.py').read_bytes()
    ui = (folder / 'launcher_ui.py').read_bytes()
    helper = (folder / 'game_helper.py').read_bytes()
    imports_audit = audit_direct_imports(_archive(original_bytes), {'talos_update_core.py': core, 'launcher_ui.py': ui, 'game_helper.py': helper})
    backend = {name: (ROOT / 'legacy-update' / name).read_bytes() for name in BACKEND_FILES}
    validation = json.loads(backend['game-validation.json'])
    _require(validation.get('runtimeModuleHash') == sha256(backend['game-runtime.cjs']), 'Backend differs from its validation report')
    metadata = json.loads(backend['game-metadata.json'])
    _require(len(metadata.get('tools', [])) == 28 and len(metadata.get('phases', [])) == 8, 'Confirmation metadata incomplete')
    application, app_audit = build_app(original_bytes, version, helper)
    launcher, launcher_audit = build_launcher(original_bytes, core, ui, public_key, feed_url)
    release = f'releases/{version}'
    payload = {app_audit['entry']: application, f'{release}/talos_game_update.py': helper}
    payload.update({f'{release}/training/{name}': content for name, content in backend.items()})
    config = {'schema': 'talos-updater-config-v1', 'feed_url': feed_url}
    launcher_files = {LAUNCHER_NAME: launcher, 'talos-updater/talos_update_core.py': core, 'talos-updater/launcher_ui.py': ui, 'talos-updater/config.json': (json.dumps(config, ensure_ascii=False, indent=2) + '\n').encode()}
    output_dir = output_dir.resolve()
    _require(not output_dir.is_relative_to(original_path.resolve().parent), 'Never write build output into the original application directory')
    records = {**{'payload/' + name: value for name, value in payload.items()}, **{'launcher/' + name: value for name, value in launcher_files.items()}}
    if output_dir.exists():
        existing = {str(path.relative_to(output_dir)).replace('\\', '/') for path in output_dir.rglob('*') if path.is_file()}
        _require(existing.issubset(set(records) | {'build-report.json'}), 'Output directory has unrelated files; select a fresh staging directory')
    for name, content in records.items():
        target = output_dir / name
        target.parent.mkdir(parents=True, exist_ok=True)
        staged = target.with_name(target.name + '.building')
        staged.write_bytes(content)
        staged.replace(target)
    report = {'schema': 'talos-online-build-v1', 'builtAt': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'version': version, 'app': app_audit, 'launcher': launcher_audit, 'payloadFiles': _file_records(payload), 'launcherFiles': _file_records(launcher_files), 'importAvailability': imports_audit, 'originalRuntimeSourceAndPolicyNotRedistributed': True, 'signingPerformed': False, 'networkOrPublicationPerformed': False}
    (output_dir / 'build-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    _require(sha256(original_path.read_bytes()) == _legacy().EXPECTED_ORIGINAL_SHA256, 'Original executable changed')
    return report


def self_test(original_path: Path):
    original = original_path.read_bytes()
    helper = b"def calculate(app, keep_status=False):\n    app.calls.append((str(app._talos_install_root), str(app._talos_base_root), keep_status))\n    return keep_status\n"
    core = b"class Updater:\n    def __init__(self, *args, **kwargs):\n        raise AssertionError('Launcher must not construct Updater in main')\n"
    ui = b"import talos_update_core\nCALLS=[]\ndef run(root, public_key_pem, config=None):\n    CALLS.append((str(root), public_key_pem, config))\n    return 0\n"
    key = (Path(__file__).resolve().parent / 'update-public-key.pem').read_text(encoding='utf-8')
    app_bytes, app_audit = build_app(original, '0.3.2', helper)
    launcher_bytes, launcher_audit = build_launcher(original, core, ui, key, DEFAULT_FEED_URL)
    audit_direct_imports(_archive(original), {'safe.py': b'import os,sys,re,unicodedata\nimport urllib.request\n'})
    try:
        audit_direct_imports(_archive(original), {'missing.py': b'import uuid\n'})
    except ValueError as error:
        _require('Import unavailable' in str(error), 'Missing import failed incorrectly')
    else:
        raise ValueError('Unbundled uuid import accepted')
    previous = {name: sys.modules.get(name) for name in ['talos_update_core', 'launcher_ui']}
    helper_module = '_talos_release_0_3_2_' + sha256(helper)[:16]
    try:
        with tempfile.TemporaryDirectory(prefix='talos-online-builder-test-') as temporary:
            directory = Path(temporary)
            module_folder = directory / 'talos-updater'
            module_folder.mkdir()
            (module_folder / 'talos_update_core.py').write_bytes(core)
            (module_folder / 'launcher_ui.py').write_bytes(ui)
            namespace = {'__name__': 'talos_launcher_static_test', '__file__': str(directory / 'launcher.py')}
            code = marshal.loads(_legacy().Archive.read(launcher_bytes).entry('app').content())
            # This is our new launcher source with synthetic core/UI only.
            exec(code, namespace)
            _require(namespace['main']() == 0, 'Synthetic launcher did not start')
            _require(sys.modules['launcher_ui'].CALLS[0][0] == str(directory), 'Launcher used wrong root')
            _require(sys.modules['launcher_ui'].CALLS[0][1] == key, 'Launcher did not pass embedded public key')
            (module_folder / 'launcher_ui.py').write_bytes(b"raise AssertionError('tampered UI executed')\n")
            try:
                namespace['main']()
            except RuntimeError as error:
                _require('校验失败' in str(error), 'Wrong tamper failure')
            else:
                raise ValueError('Tampered launcher module accepted')
            release = directory / 'releases' / '0.3.2'
            release.mkdir(parents=True)
            (release / 'talos_game_update.py').write_bytes(helper)
            class App:
                calls = []
                class status:
                    @staticmethod
                    def set(message):
                        raise AssertionError(message)
            class MessageBox:
                @staticmethod
                def showerror(title, message):
                    raise AssertionError(message)
            app = App()
            function = types.FunctionType(app_loader_code('0.3.2', sha256(helper)), {'ROOT': directory, 'messagebox': MessageBox()}, argdefs=(False,))
            _require(function(app, keep_status=True) is True, 'App loader did not preserve keep_status')
            _require(app.calls == [(str(release), str(directory), True)], 'App release/base roots are mixed')
            _require(function(app) is False, 'App helper cache did not work')
            for invalid in ['../0.3.2', '0.3.2/other', '00.3.2']:
                try:
                    app_loader_code(invalid, sha256(helper))
                except ValueError:
                    pass
                else:
                    raise ValueError('Invalid version accepted')
    finally:
        sys.modules.pop(helper_module, None)
        for name, value in previous.items():
            if value is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = value
    _require(sha256(original_path.read_bytes()) == _legacy().EXPECTED_ORIGINAL_SHA256, 'Original EXE modified')
    return {'passed': True, 'appBytes': len(app_bytes), 'launcherBytes': len(launcher_bytes), 'archivePrefixesAndPyzPreserved': True, 'pinnedModuleTamperRejected': True, 'versionRootsSeparated': True, 'unbundledImportRejected': True, 'oldApplicationCodeExecuted': False, 'onlyNewSyntheticCodeExecuted': True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', type=Path, default=DEFAULT_ORIGINAL)
    parser.add_argument('--version', default='0.3.3')
    parser.add_argument('--public-key', type=Path, default=Path(__file__).resolve().parent / 'update-public-key.pem')
    parser.add_argument('--feed-url', default=DEFAULT_FEED_URL)
    parser.add_argument('--output', type=Path, default=ROOT / 'dist' / 'online-0.3.3')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    try:
        if args.self_test:
            print(json.dumps(self_test(args.original), ensure_ascii=False))
        else:
            report = build_staging(args.original, args.version, args.public_key, args.feed_url, args.output)
            print(json.dumps({'passed': True, 'output': str(args.output), 'version': args.version, 'app': report['app']['executableSha256'], 'launcher': report['launcher']['executableSha256'], 'payloadFiles': len(report['payloadFiles']), 'launcherFiles': len(report['launcherFiles'])}, ensure_ascii=False))
        return 0
    except (OSError, ValueError, StopIteration, TypeError, EOFError) as error:
        print('Online build failed: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
