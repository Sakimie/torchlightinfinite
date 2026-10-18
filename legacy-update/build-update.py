#!/usr/bin/env python3
"""Build a reversible Talos v0.3 update without executing its bundled code.

The only Python execution in --self-test is the newly authored loader with a
synthetic helper. The original EXE and its Python modules are never executed.
"""
from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
import datetime
import hashlib
import gzip
import importlib.util
import json
import io
import marshal
from pathlib import Path, PurePosixPath
import struct
import sys
import tempfile
import types
import zipfile
import zlib

EXPECTED_ORIGINAL_SHA256 = '412b6fe55026d69eb150f65eabbd1e4b72b8aea74be776e5eadbfb9f35f01503'
COOKIE_MAGIC = b'MEI\x0c\x0b\x0a\x0b\x0e'
COOKIE = struct.Struct('!8sIIII64s')
TOC_HEADER = struct.Struct('!IIIIBc')
REPOSITORY = Path(__file__).resolve().parent.parent
NEW_EXE_NAME = 'TalosHelper-v0.3.1.exe'
REPLACEMENTS = {
    '塔罗斯决策助手 · Demo 0.3 英文规则': '塔罗斯助手 · 0.3.1 截图策略接入试用',
    '本地识别 · 可修正沙盘 · 自动比较连续吞噬': '截图须核对后使用修正规则 · 英文存档仍为历史策略',
    'Demo 0.3 · 新策略用于英文存档建议；截图模块尚未迁移。': '0.3.1 · 截图须核对后使用修正规则；英文存档仍为历史策略。',
    '\n\n该建议使用存档本身的练习参数和训练所得权重。左侧是独立的实机截图编辑器；点击“计算下一步”会回到实机策略。':
        '\n\n英文存档建议仍使用历史规则与训练权重；截图须核对完整状态后，点击“计算下一步”使用修正规则。',
}
PAYLOAD_MAPPING = {
    'talos_game_update.py': 'talos_game_update.py',
    'game-runtime.cjs': 'training/game-runtime.cjs',
    'game-advice.cjs': 'training/game-advice.cjs',
    'game-metadata.json': 'training/game-metadata.json',
    'game-validation.json': 'training/game-validation.json',
    'game-validation.md': 'training/game-validation.md',
    'game-validation.rows.csv.gz': 'training/game-validation.rows.csv.gz',
    '旧版审查记录.md': '旧版审查记录.md',
    '更新说明.txt': '更新说明.txt',
}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def require(condition: object, message: str) -> None:
    if not condition:
        raise ValueError(message)


def decompress_checked(packed: bytes, expected: int) -> bytes:
    require(0 <= expected <= 64 * 1024 * 1024, 'Archive entry exceeds the extraction bound')
    decoder = zlib.decompressobj()
    result = decoder.decompress(packed, expected + 1)
    require(len(result) == expected and decoder.eof and not decoder.unused_data, 'Invalid compressed archive entry')
    return result


@dataclass(frozen=True)
class Entry:
    name: str
    kind: bytes
    compressed: int
    unpacked_size: int
    packed: bytes
    original_toc: bytes

    def content(self) -> bytes:
        return decompress_checked(self.packed, self.unpacked_size) if self.compressed else self.packed


@dataclass(frozen=True)
class Archive:
    prefix: bytes
    python_version: int
    python_library: bytes
    entries: tuple[Entry, ...]

    @classmethod
    def read(cls, data: bytes) -> 'Archive':
        cookie_pos = data.rfind(COOKIE_MAGIC)
        require(cookie_pos >= 0 and cookie_pos + COOKIE.size == len(data), 'Missing or misplaced CArchive cookie')
        magic, total_size, toc_offset, toc_size, python_version, python_library = COOKIE.unpack_from(data, cookie_pos)
        require(magic == COOKIE_MAGIC and python_version == 312, 'Expected Python 3.12 CArchive')
        require(sys.version_info[:2] == (3, 12), 'Build with Python 3.12; never reinterpret a different marshal version')
        start = len(data) - total_size
        toc_start = start + toc_offset
        toc_end = toc_start + toc_size
        require(0 < start <= toc_start <= toc_end == cookie_pos, 'Invalid CArchive bounds')
        cursor = toc_start
        entries = []
        names = set()
        while cursor < toc_end:
            require(cursor + TOC_HEADER.size <= toc_end, 'Truncated CArchive TOC')
            length, offset, packed_size, unpacked_size, compressed, kind = TOC_HEADER.unpack_from(data, cursor)
            require(length > TOC_HEADER.size and cursor + length <= toc_end, 'Invalid CArchive TOC entry')
            toc_record = data[cursor:cursor+length]
            encoded_name = toc_record[TOC_HEADER.size:].split(b'\0', 1)[0]
            name = encoded_name.decode('utf-8')
            safe_name = name.replace('\\', '/')
            require(not PurePosixPath(safe_name).is_absolute() and '..' not in PurePosixPath(safe_name).parts, 'Unsafe archive member name')
            require(name not in names and compressed in [0, 1], 'Duplicate member or unsupported compression flag')
            names.add(name)
            entry_start = start + offset
            require(start <= entry_start <= entry_start + packed_size <= toc_start, 'Invalid CArchive member bounds')
            require(compressed or packed_size == unpacked_size, 'Uncompressed archive size mismatch')
            entries.append(Entry(name, kind, compressed, unpacked_size, data[entry_start:entry_start+packed_size], toc_record))
            cursor += length
        require(cursor == toc_end, 'CArchive TOC ended incorrectly')
        pyz = next((entry for entry in entries if entry.kind == b'z'), None)
        require(pyz is not None and pyz.content()[:8] == b'PYZ\0' + importlib.util.MAGIC_NUMBER, 'PYZ magic differs from the build interpreter')
        return cls(data[:start], python_version, python_library, tuple(entries))

    def entry(self, name: str) -> Entry:
        matches = [entry for entry in self.entries if entry.name == name]
        require(len(matches) == 1, 'Expected one CArchive member: ' + name)
        return matches[0]

    def rebuild(self, replacements: dict[str, bytes]) -> bytes:
        require(set(replacements).issubset({entry.name for entry in self.entries}), 'Cannot add unexpected archive entries')
        chunks = []
        toc = []
        offset = 0
        for entry in self.entries:
            replacement = replacements.get(entry.name)
            if replacement is None:
                packed, unpacked_size = entry.packed, entry.unpacked_size
            else:
                packed = zlib.compress(replacement, 9) if entry.compressed else replacement
                unpacked_size = len(replacement)
            record = TOC_HEADER.pack(len(entry.original_toc), offset, len(packed), unpacked_size, entry.compressed, entry.kind)
            # Preserve the exact name, null terminator and padding bytes.
            toc.append(record + entry.original_toc[TOC_HEADER.size:])
            chunks.append(packed)
            offset += len(packed)
        table = b''.join(toc)
        cookie = COOKIE.pack(COOKIE_MAGIC, offset + len(table) + COOKIE.size, offset, len(table), self.python_version, self.python_library)
        return self.prefix + b''.join(chunks) + table + cookie


def loader_code(helper_sha256: str) -> types.CodeType:
    require(len(helper_sha256) == 64 and all(c in '0123456789abcdef' for c in helper_sha256), 'Invalid helper SHA256')
    source = '''def calculate(self, keep_status=False):
    try:
        import hashlib
        import importlib.util
        from pathlib import Path
        import sys
        expected = __HELPER_HASH__
        directory = Path(sys.executable).resolve().parent if getattr(sys, 'frozen', False) else Path(ROOT).resolve()
        helper = directory / 'talos_game_update.py'
        module_name = '_talos_game_update_v031_' + expected[:16]
        module = sys.modules.get(module_name)
        if module is None:
            content = helper.read_bytes()
            if hashlib.sha256(content).hexdigest() != expected:
                raise RuntimeError('升级组件校验失败，请重新解压完整的 0.3.1 更新包。')
            spec = importlib.util.spec_from_file_location(module_name, str(helper))
            if spec is None or spec.loader is None or not hasattr(spec.loader, 'source_to_code'):
                raise RuntimeError('无法读取升级组件，请将更新包解压到原助手目录。')
            module = importlib.util.module_from_spec(spec)
            # Compile the verified in-memory bytes; never reread after hashing.
            code = spec.loader.source_to_code(content, str(helper))
            sys.modules[module_name] = module
            try:
                exec(code, module.__dict__)
                if not callable(getattr(module, 'calculate', None)):
                    raise RuntimeError('升级组件缺少计算接口。')
                module.__talos_verified_sha256__ = expected
            except BaseException:
                sys.modules.pop(module_name, None)
                raise
        elif getattr(module, '__talos_verified_sha256__', None) != expected:
            raise RuntimeError('升级组件缓存校验失败，请关闭并重新打开助手。')
        return module.calculate(self, keep_status=keep_status)
    except Exception as error:
        message = '截图策略组件加载失败：' + str(error)
        self.status.set(message)
        messagebox.showerror('0.3.1 更新组件', message)
'''.replace('__HELPER_HASH__', repr(helper_sha256))
    compiled = compile(source, 'talos_game_update_loader.py', 'exec', dont_inherit=True)
    code = next(value for value in compiled.co_consts if isinstance(value, types.CodeType) and value.co_name == 'calculate')
    require(code.co_argcount == 2 and code.co_varnames[:2] == ('self', 'keep_status') and not code.co_freevars, 'Loader signature differs from App.calculate')
    return code.replace(co_qualname='App.calculate')


def patch_code(original: types.CodeType, helper_sha256: str) -> tuple[types.CodeType, list[dict]]:
    replacement_counts = {old: 0 for old in REPLACEMENTS}
    edits = []
    calculate_count = 0
    replacement = loader_code(helper_sha256)

    def visit(code: types.CodeType) -> types.CodeType:
        nonlocal calculate_count
        if code.co_qualname == 'App.calculate':
            require(code.co_argcount == 2 and code.co_varnames[:2] == ('self', 'keep_status') and not code.co_freevars, 'Unexpected original calculate signature')
            calculate_count += 1
            edits.append({'function': code.co_qualname, 'change': 'Verified cached offline helper loader; preserves keep_status keyword'})
            return replacement
        constants = []
        for value in code.co_consts:
            if isinstance(value, types.CodeType):
                constants.append(visit(value))
            elif isinstance(value, str) and value in REPLACEMENTS:
                replacement_counts[value] += 1
                constants.append(REPLACEMENTS[value])
                edits.append({'function': code.co_qualname, 'change': 'Exact text constant', 'before': value, 'after': REPLACEMENTS[value]})
            else:
                constants.append(value)
        return code.replace(co_consts=tuple(constants))

    patched = visit(original)
    require(calculate_count == 1 and all(count == 1 for count in replacement_counts.values()), 'Original entry does not match the pinned function and exact text edits')
    return patched, edits


CODE_ATTRIBUTES = ['co_argcount', 'co_posonlyargcount', 'co_kwonlyargcount', 'co_nlocals', 'co_stacksize', 'co_flags', 'co_code', 'co_names', 'co_varnames', 'co_filename', 'co_name', 'co_qualname', 'co_firstlineno', 'co_linetable', 'co_exceptiontable', 'co_freevars', 'co_cellvars']


def code_identity(code: types.CodeType) -> tuple:
    # Marshal's string-reference flags may differ after a read/write cycle;
    # compare actual code fields and constants instead of serialized intern IDs.
    constants = tuple(code_identity(value) if isinstance(value, types.CodeType) else value for value in code.co_consts)
    return tuple(getattr(code, attribute) for attribute in CODE_ATTRIBUTES) + (constants,)


def compare_code(original: types.CodeType, patched: types.CodeType, helper_sha256: str) -> None:
    if original.co_qualname == 'App.calculate':
        require(code_identity(patched) == code_identity(loader_code(helper_sha256)), 'Patched calculate is not the expected verified loader')
        return
    for attribute in CODE_ATTRIBUTES:
        require(getattr(original, attribute) == getattr(patched, attribute), 'Unexpected code change: ' + original.co_qualname + '/' + attribute)
    require(len(original.co_consts) == len(patched.co_consts), 'Unexpected constants count change')
    for before, after in zip(original.co_consts, patched.co_consts):
        if isinstance(before, types.CodeType):
            require(isinstance(after, types.CodeType), 'Code object replaced with non-code')
            compare_code(before, after, helper_sha256)
        elif isinstance(before, str) and before in REPLACEMENTS:
            require(after == REPLACEMENTS[before], 'Text replacement differs from the specified change')
        else:
            require(before == after, 'Unspecified constant changed')


def build_executable(original_bytes: bytes, helper_sha256: str) -> tuple[bytes, dict]:
    require(digest(original_bytes) == EXPECTED_ORIGINAL_SHA256, 'Original EXE SHA256 differs: refuse to patch a different version')
    archive = Archive.read(original_bytes)
    entry = archive.entry('app')
    require(entry.kind == b's', 'Expected app script entry')
    original_code = marshal.loads(entry.content())
    require(isinstance(original_code, types.CodeType), 'Entry is not a code object')
    patched_code, edits = patch_code(original_code, helper_sha256)
    executable = archive.rebuild({'app': marshal.dumps(patched_code)})
    rebuilt = Archive.read(executable)
    require(rebuilt.prefix == archive.prefix, 'PE bootloader prefix changed')
    require([entry.name for entry in rebuilt.entries] == [entry.name for entry in archive.entries], 'Archive members changed')
    unchanged = []
    for before, after in zip(archive.entries, rebuilt.entries):
        require(before.kind == after.kind and before.compressed == after.compressed, 'Archive member metadata changed')
        require(before.original_toc[TOC_HEADER.size:] == after.original_toc[TOC_HEADER.size:], 'Archive member name or padding changed')
        if before.name != 'app':
            require(before.packed == after.packed and before.unpacked_size == after.unpacked_size, 'Non-app compressed archive member changed: ' + before.name)
            unchanged.append({'name': before.name, 'packedSha256': digest(before.packed)})
    compare_code(original_code, marshal.loads(rebuilt.entry('app').content()), helper_sha256)
    return executable, {
        'sourceExeSha256': EXPECTED_ORIGINAL_SHA256,
        'updatedExeSha256': digest(executable),
        'updatedExeBytes': len(executable),
        'pythonVersion': '3.12',
        'pePrefixSha256': digest(archive.prefix),
        'pePrefixUnchanged': True,
        'pyzBytesUnchanged': True,
        'onlyModifiedArchiveEntry': 'app',
        'codeEdits': edits,
        'unchangedCompressedMembers': unchanged,
        'helperSha256PinnedInLoader': helper_sha256,
        'newExeName': NEW_EXE_NAME,
        'oldExeOverwritten': False,
        'oldEmbeddedCodeExecutedDuringBuild': False,
        'windowsExecutableExecutedDuringBuild': False,
    }


def self_test(original_file: Path) -> dict:
    original_bytes = original_file.read_bytes()
    require(Archive.read(original_bytes).rebuild({}) == original_bytes, 'Unmodified archive did not round-trip byte for byte')
    tampered_original = original_bytes[:-1] + bytes([original_bytes[-1] ^ 1])
    try:
        build_executable(tampered_original, '0' * 64)
    except ValueError as error:
        require('SHA256 differs' in str(error), 'Wrong-version input failed for the wrong reason')
    else:
        raise ValueError('Modified original EXE was accepted')
    helper_content = b"LOAD_COUNT = 1\ndef calculate(app, keep_status=False):\n    app.calls.append(keep_status)\n    return {'loaded': LOAD_COUNT, 'keep_status': keep_status}\n"
    helper_hash = digest(helper_content)
    executable, report = build_executable(original_bytes, helper_hash)
    with tempfile.TemporaryDirectory(prefix='talos-v031-builder-test-') as name:
        directory = Path(name)
        staged_exe = directory / NEW_EXE_NAME
        staged_exe.write_bytes(executable)
        require(Archive.read(staged_exe.read_bytes()).entry('PYZ.pyz').packed == Archive.read(original_bytes).entry('PYZ.pyz').packed, 'Staged EXE differs from rebuilt archive')
        helper = directory / 'talos_game_update.py'
        helper.write_bytes(helper_content)
        errors = []
        class Status:
            def set(self, message):
                errors.append(message)
        class FakeApp:
            status = Status()
            calls = []
        class MessageBox:
            def showerror(self, title, message):
                errors.append(title + ': ' + message)
        app = FakeApp()
        # Execute only the newly authored wrapper and this synthetic helper.
        function = types.FunctionType(loader_code(helper_hash), {'ROOT': directory, 'messagebox': MessageBox()}, argdefs=(False,))
        module_name = '_talos_game_update_v031_' + helper_hash[:16]
        try:
            require(function(app, keep_status=True) == {'loaded': 1, 'keep_status': True}, 'Loader did not forward keep_status')
            helper.write_bytes(b"raise AssertionError('must not reread cached helper')\n")
            require(function(app) == {'loaded': 1, 'keep_status': False}, 'Verified helper was not cached')
            require(not errors and app.calls == [True, False], 'Loader did not preserve the expected calls')
            sys.modules.pop(module_name, None)
            require(function(app) is None and any('校验失败' in message for message in errors), 'Modified helper was not rejected before execution')
            helper.write_bytes(helper_content)
            errors.clear()
            original_executable = sys.executable
            had_frozen = hasattr(sys, 'frozen')
            original_frozen = getattr(sys, 'frozen', None)
            try:
                sys.executable = str(staged_exe)
                sys.frozen = True
                function.__globals__['ROOT'] = directory / 'incorrect-development-root'
                require(function(app, keep_status=True)['keep_status'] is True and not errors, 'Frozen loader did not use executable parent directory')
            finally:
                sys.executable = original_executable
                if had_frozen:
                    sys.frozen = original_frozen
                else:
                    del sys.frozen
        finally:
            sys.modules.pop(module_name, None)
    require(digest(original_file.read_bytes()) == EXPECTED_ORIGINAL_SHA256, 'Original EXE was modified')
    report['selfTest'] = {'passed': True, 'newLoaderAndSyntheticHelperOnlyExecuted': True, 'originalCodeNeverExecuted': True, 'helperTamperRejected': True, 'moduleCachedAcrossClicks': True, 'keepStatusForwarded': True, 'frozenPathUsesExecutableParent': True, 'stagedExeReparsed': True, 'unmodifiedArchiveRoundTripExact': True, 'wrongOriginalHashRefused': True}
    return report


def build_zip(original_file: Path, payload_dir: Path, output: Path) -> dict:
    require(output.resolve() != original_file.resolve(), 'Refuse to overwrite the original EXE')
    payload = {}
    for source_name, destination in PAYLOAD_MAPPING.items():
        source = payload_dir / source_name
        require(source.is_file(), 'Update payload not ready: ' + str(source))
        payload[destination] = source.read_bytes()
    metadata = json.loads(payload['training/game-metadata.json'])
    for group in ['tools', 'events', 'gifts', 'objectives', 'phases']:
        records = metadata.get(group)
        require(isinstance(records, list) and all(isinstance(item, dict) and isinstance(item.get('id'), str) and isinstance(item.get('name'), str) for item in records), 'Incomplete confirmation metadata: ' + group)
        require(len({item['id'] for item in records}) == len(records), 'Duplicate confirmation metadata IDs: ' + group)
    require(len(metadata['tools']) == 28, 'Confirmation dialog must expose all 28 tool IDs')
    require(len(metadata['gifts']) == 4 and len(metadata['phases']) == 8, 'Confirmation gift/phase metadata is incomplete')
    validation = json.loads(payload['training/game-validation.json'])
    require(isinstance(validation, dict), 'Validation report must be a JSON object')
    records = validation.get('records', {})
    rows_packed = payload['training/game-validation.rows.csv.gz']
    require(records.get('file') == 'game-validation.rows.csv.gz', 'Validation row filename differs from the supplied report')
    require(records.get('bytes') == len(rows_packed) and records.get('sha256') == digest(rows_packed), 'Validation row gzip SHA256/size differs from the report')
    rows_unpacked = gzip.decompress(rows_packed)
    require(records.get('uncompressedBytes') == len(rows_unpacked) and records.get('uncompressedSha256') == digest(rows_unpacked), 'Uncompressed validation rows differ from the report')
    reader = csv.DictReader(io.StringIO(rows_unpacked.decode('utf-8')))
    require(reader.fieldnames == ['index', 'seed', 'baseline_rainbows', 'historical_rainbows'], 'Unexpected validation row columns')
    rows = list(reader)
    require(len(rows) == records.get('rows') == validation['configuration']['pairs'], 'Validation row count differs from the report')
    require(len({row['seed'] for row in rows}) == len(rows), 'Duplicate validation seeds')
    require([int(row['index']) for row in rows] == list(range(len(rows))), 'Validation indices are not consecutive')
    require(validation.get('runtimeModuleHash') == digest(payload['training/game-runtime.cjs']), 'Validation runtime hash differs from the packaged runtime')
    executable, report = build_executable(original_file.read_bytes(), digest(payload['talos_game_update.py']))
    payload[NEW_EXE_NAME] = executable
    report['builtAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    report['packageScope'] = 'Side-by-side v0.3.1 executable and new bridge files only; reuse the complete existing v0.3 directory and its runtimes.'
    report['confirmationMetadata'] = {group: len(metadata[group]) for group in ['tools', 'events', 'gifts', 'objectives', 'phases']}
    report['policyValidation'] = {
        'selected': validation['selection']['selected'],
        'pairedSeeds': len(rows),
        'fullGameEvaluations': validation['configuration']['fullGameEvaluations'],
        'runtimeModuleHashVerified': True,
        'recordsCompressedSha256Verified': records['sha256'],
        'recordsUncompressedSha256Verified': records['uncompressedSha256'],
        'recordCountAndUniqueSeedsVerified': True,
    }
    report['files'] = [{'name': name, 'bytes': len(content), 'sha256': digest(content)} for name, content in sorted(payload.items())]
    report['windowsGameValidation'] = 'Not run by this builder; archive reconstruction and new-loader tests are separate from Windows game verification.'
    payload['更新包清单.json'] = (json.dumps(report, indent=2, ensure_ascii=False) + '\n').encode()
    require('TalosHelper.exe' not in payload and not any(name.startswith(('runtime/', '_internal/', 'assets/', 'english-source/')) for name in payload), 'Update unexpectedly replaces original runtime/source/assets')
    output.parent.mkdir(parents=True, exist_ok=True)
    staged_zip = output.with_suffix(output.suffix + '.tmp')
    try:
        with zipfile.ZipFile(staged_zip, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, content in sorted(payload.items()):
                archive.writestr(name, content)
        with zipfile.ZipFile(staged_zip) as archive:
            require(archive.testzip() is None, 'Update ZIP CRC check failed')
            for name, content in payload.items():
                require(archive.read(name) == content, 'Update ZIP entry bytes differ: ' + name)
        staged_zip.replace(output)
    finally:
        staged_zip.unlink(missing_ok=True)
    require(digest(original_file.read_bytes()) == EXPECTED_ORIGINAL_SHA256, 'Original EXE was modified')
    report['zip'] = {'path': str(output), 'bytes': output.stat().st_size, 'sha256': digest(output.read_bytes()), 'crcValid': True}
    audit_path = output.with_suffix('.audit.json')
    audit_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n')
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', type=Path, default=Path('/workspace/legacy-talos-v03/TalosHelper.exe'))
    parser.add_argument('--payload-dir', type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument('--output', type=Path, default=REPOSITORY / 'dist/TalosHelper-v0.3.1-Offline-Update.zip')
    parser.add_argument('--self-test', action='store_true', help='Rebuild a temporary EXE and exercise only the new wrapper with a synthetic helper')
    args = parser.parse_args()
    try:
        report = self_test(args.original) if args.self_test else build_zip(args.original, args.payload_dir, args.output)
        print(json.dumps({'passed': True, 'selfTest': report.get('selfTest'), 'zip': report.get('zip'), 'updatedExeBytes': report['updatedExeBytes'], 'oldCodeExecuted': False}, ensure_ascii=False))
        return 0
    except (OSError, ValueError, StopIteration, TypeError, EOFError, zlib.error) as error:
        print('Update build failed: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
