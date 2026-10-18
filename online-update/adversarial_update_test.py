"""Independent adversarial tests. All release keys and installation files are temporary.

No publication key is read. Test signatures come from cryptography's Ed25519;
the updater independently verifies them through its normal Node verifier.
"""
from __future__ import annotations
import base64
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import urllib.request
import warnings
import zipfile
from types import SimpleNamespace
from unittest import mock

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("talos_adversarial_core", HERE / "talos_update_core.py")
core = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(core)


def digest(data):
    return hashlib.sha256(data).hexdigest()


class AdversarialUpdates(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="talos-adversarial-")
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.root = self.home / "installation"
        self.root.mkdir()
        self.key = Ed25519PrivateKey.generate()
        self.public = self.key.public_key().public_bytes(Encoding.PEM, PublicFormat.SubjectPublicKeyInfo)
        self.requests = []
        self.responses = {}
        self.node = shutil.which("node")
        if not self.node:
            self.skipTest("Node is required for the normal signature verifier")
        # Only synthetic local dependencies are used; no original package is changed.
        dependencies = {}
        for i, name in enumerate(core.BASE_DEPENDENCIES):
            data = f"fixed-local-test-dependency-{i}\n".encode()
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            dependencies[name] = (len(data), digest(data))
        patch = mock.patch.dict(core.BASE_DEPENDENCIES, dependencies, clear=True)
        patch.start()
        self.addCleanup(patch.stop)
        self.updater = core.Updater(self.root, self.public, "https://updates.example/latest.json", self.node, self.transport)

    def transport(self, url, maximum):
        self.requests.append(url)
        value = self.responses[url]
        if len(value) > maximum:
            raise core.NetworkError("synthetic over-limit transport")
        return value

    def signed(self, manifest, key=None):
        payload = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
        return json.dumps({"schema": "talos-signed-feed-v1", "payload": base64.b64encode(payload).decode(),
                           "signature": base64.b64encode((key or self.key).sign(payload)).decode()}).encode()

    def release(self, version="0.3.2", *, extra=None, entries=None, mutate=None, key=None):
        prefix = f"releases/{version}/"
        files = {
            f"TalosHelper-App-{version}.exe": b"MZ-test-application-" + version.encode(),
            prefix + "talos_game_update.py": b"SAFE_TEST_MARKER = 1\n",
            prefix + "training/game-runtime.cjs": b"module.exports = {};\n",
        }
        if extra:
            files.update(extra)
        archive = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as out:
                for name, value in entries if entries is not None else files.items():
                    out.writestr(name, value)
        package = archive.getvalue()
        manifest = {"schema": "talos-release-v1", "app": "TalosHelper", "version": version,
                    "launcher_min": "1.0.0", "base_version": "0.3", "published_at": "2026-10-17T00:00:00Z",
                    "notes": ["Temporary independent test release"], "entry": f"TalosHelper-App-{version}.exe",
                    "package": {"url": f"https://cdn.example/{version}.zip", "bytes": len(package), "sha256": digest(package)},
                    "files": [{"path": name, "bytes": len(value), "sha256": digest(value)} for name, value in files.items()]}
        if mutate:
            mutate(manifest)
        feed = self.signed(manifest, key)
        serial = len(list(self.home.glob("feed-*.json")))
        feed_path, package_path = self.home / f"feed-{serial}.json", self.home / f"package-{serial}.zip"
        feed_path.write_bytes(feed)
        package_path.write_bytes(package)
        self.responses[self.updater.feed_url] = feed
        self.responses[manifest["package"]["url"]] = package
        return SimpleNamespace(manifest=manifest, feed=feed, package=package, feed_path=feed_path, package_path=package_path)

    def install(self, version="0.3.2"):
        release = self.release(version)
        self.updater.install_local(release.feed_path, release.package_path)
        self.assertEqual(self.updater.status()["active"], version)
        return release

    def test_wrong_key_and_modified_payload_are_rejected(self):
        other_key = Ed25519PrivateKey.generate()
        release = self.release(key=other_key)
        with self.assertRaises(core.AuthenticationError):
            self.updater.install_local(release.feed_path, release.package_path)
        good = self.release()
        envelope = json.loads(good.feed)
        payload = json.loads(base64.b64decode(envelope["payload"]))
        payload["notes"] = ["Tampered after signature"]
        envelope["payload"] = base64.b64encode(json.dumps(payload).encode()).decode()
        good.feed_path.write_text(json.dumps(envelope))
        with self.assertRaises(core.AuthenticationError):
            self.updater.install_local(good.feed_path, good.package_path)
        self.assertFalse((self.root / "talos-update-state.json").exists())

    def test_check_then_mutate_and_plain_manifest_cannot_install(self):
        release = self.release()
        with self.assertRaises(core.AuthenticationError):
            self.updater.install(copy.deepcopy(release.manifest))
        authenticated = self.updater.check()
        authenticated["files"][0]["sha256"] = "0" * 64
        with self.assertRaises(core.AuthenticationError):
            self.updater.install(authenticated)
        self.assertEqual(self.requests, [self.updater.feed_url])

    def test_state_retains_signature_and_tampering_cannot_launch(self):
        self.install()
        state_path = self.root / "talos-update-state.json"
        state = json.loads(state_path.read_text())
        record = state["installed"]["0.3.2"]["feed"]
        manifest = json.loads(base64.b64decode(record["payload"]))
        manifest["entry"] = "untrusted.exe"
        record["payload"] = base64.b64encode(json.dumps(manifest).encode()).decode()
        state_path.write_text(json.dumps(state))
        with self.assertRaises(core.AuthenticationError):
            self.updater.launch_active()

    def test_install_rejects_downgrade_and_changed_same_version_identity(self):
        self.install("0.3.3")
        old = self.release("0.3.2")
        with self.assertRaises(core.UpdateError):
            self.updater.install_local(old.feed_path, old.package_path)
        self.assertEqual(self.updater.status()["active"], "0.3.3")
        changed = self.release("0.3.3", mutate=lambda m: m["notes"].append("different identity"))
        with self.assertRaises(core.UpdateError):
            self.updater.install_local(changed.feed_path, changed.package_path)

    def test_windows_reserved_and_alias_paths_rejected(self):
        bad = ["../escape.py", "/absolute.py", "C:drive.py", "x\\y.py", "x:stream", "tail.", "tail ",
               "NUL.txt", "COM1.py", "LPT¹.json", "CON .txt", "NUL .exe", "COM1 .py", "CONIN$", "CONOUT$"]
        accepted = []
        for name in bad:
            try:
                core._safe_name("releases/0.3.2/" + name)
            except core.ValidationError:
                continue
            accepted.append(name)
        self.assertEqual(accepted, [], f"Unsafe Windows names accepted: {accepted}")

    def test_manifest_case_and_file_directory_collisions_rejected(self):
        for extra in ({"releases/0.3.2/A.py": b"a", "releases/0.3.2/a.py": b"b"},
                      {"releases/0.3.2/p": b"a", "releases/0.3.2/p/q.py": b"b"}):
            release = self.release(extra=extra)
            with self.assertRaises(core.ValidationError):
                self.updater.install_local(release.feed_path, release.package_path)

    def test_protected_dependency_namespace_is_case_insensitive(self):
        for name in ("English-Source/js/engine.js", "Training/English/policy.json"):
            release = self.release(extra={"releases/0.3.2/" + name: b"untrusted"})
            with self.assertRaises(core.ValidationError):
                self.updater._manifest(json.loads(release.feed))

    def test_zip_duplicates_unlisted_files_symlinks_and_bombs_rejected(self):
        version = "0.3.2"
        regular = self.release(version)
        original_entries = [(item["path"], b"x" * item["bytes"]) for item in regular.manifest["files"]]
        variants = [original_entries + [original_entries[0]], original_entries + [("unlisted.txt", b"x")]]
        link = zipfile.ZipInfo("releases/0.3.2/talos_game_update.py")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        variants.append([(name if name != link.filename else link, value) for name, value in original_entries])
        for entries in variants:
            release = self.release(version, entries=entries)
            with self.assertRaises(core.ValidationError):
                self.updater.install_local(release.feed_path, release.package_path)
        bomb = self.release(version, extra={"releases/0.3.2/compressed.bin": b"A" * (2 * 1024 * 1024)})
        with self.assertRaises(core.ValidationError):
            self.updater.install_local(bomb.feed_path, bomb.package_path)
        self.assertFalse((self.root / "talos-update-state.json").exists())

    def test_oversized_and_incompatible_signed_manifests_rejected(self):
        for mutate in (lambda m: m.update(launcher_min="9.0.0"),
                       lambda m: m["package"].update(bytes=core.MAX_PACKAGE + 1),
                       lambda m: m["files"][0].update(bytes=core.MAX_FILE + 1)):
            release = self.release(mutate=mutate)
            with self.assertRaises(core.ValidationError):
                self.updater.install_local(release.feed_path, release.package_path)

    @unittest.skipUnless(hasattr(os, "symlink"), "symlinks unavailable")
    def test_existing_release_symlink_cannot_escape_installation(self):
        outside = self.home / "outside"
        outside.mkdir()
        try:
            (self.root / "releases").symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest("symlink permission unavailable")
        release = self.release()
        with self.assertRaises(core.ValidationError):
            self.updater.install_local(release.feed_path, release.package_path)
        self.assertEqual(list(outside.iterdir()), [])
        self.assertFalse((self.root / "talos-update-state.json").exists())

    def test_redirect_downgrades_and_credentials_blocked_before_request(self):
        handler = core._Redirect()
        request = urllib.request.Request("https://updates.example/feed")
        for url in ("http://updates.example/pkg", "file:///tmp/package.zip", "ftp://updates.example/pkg",
                    "https://user:password@updates.example/pkg", "https://updates.example:444/pkg"):
            with self.assertRaises(core.ValidationError):
                handler.redirect_request(request, None, 302, "redirect", {}, url)
        self.assertEqual(handler.redirect_request(request, None, 302, "redirect", {}, "https://cdn.example/pkg").full_url,
                         "https://cdn.example/pkg")

    def test_bootstrap_status_rollback_and_launch_do_not_network(self):
        first = self.release("0.3.2")
        bootstrap = self.root / "talos-updater/bootstrap"
        bootstrap.mkdir(parents=True)
        (bootstrap / "latest.json").write_bytes(first.feed)
        (bootstrap / "package.zip").write_bytes(first.package)
        with mock.patch.object(core.urllib.request, "build_opener", side_effect=AssertionError("unexpected network")):
            self.assertEqual(self.updater.status()["active"], "0.3.2")
            self.install("0.3.3")
            self.assertEqual(self.updater.rollback()["active"], "0.3.2")
            real_popen = subprocess.Popen
            launches = []
            def intercept(args, *a, **kw):
                if Path(args[0]).resolve() == Path(self.node).resolve():
                    return real_popen(args, *a, **kw)
                launches.append((args, kw))
                return SimpleNamespace(pid=12345)
            with mock.patch.object(core.subprocess, "Popen", side_effect=intercept):
                self.assertEqual(self.updater.launch_active()["version"], "0.3.2")
            self.assertEqual(len(launches), 1)
            self.assertFalse(launches[0][1]["shell"])
        self.assertEqual(self.requests, [])

    def test_precommit_state_rename_failure_keeps_old_release(self):
        self.install("0.3.2")
        update = self.release("0.3.3")
        real_replace = core.os.replace
        def fail_before(source, target):
            if Path(target).name == "talos-update-state.json":
                raise OSError("synthetic failure before commit")
            return real_replace(source, target)
        with mock.patch.object(core.os, "replace", side_effect=fail_before):
            with self.assertRaises(core.UpdateError):
                self.updater.install_local(update.feed_path, update.package_path)
        self.assertEqual(self.updater.status()["active"], "0.3.2")
        self.assertFalse((self.root / "TalosHelper-App-0.3.3.exe").exists())

    def test_exception_after_successful_state_replace_never_deletes_active_files(self):
        self.install("0.3.2")
        update = self.release("0.3.3")
        real_replace = core.os.replace
        def fail_after(source, target):
            result = real_replace(source, target)
            if Path(target).name == "talos-update-state.json":
                raise OSError("synthetic exception after successful commit")
            return result
        with mock.patch.object(core.os, "replace", side_effect=fail_after):
            try:
                self.updater.install_local(update.feed_path, update.package_path)
            except core.UpdateError:
                pass
        # Either complete version is safe; a state pointing to cleaned-up files is not.
        self.assertIn(self.updater.status()["active"], ("0.3.2", "0.3.3"))

    def test_cancel_before_activation_does_not_change_state(self):
        self.install("0.3.2")
        self.release("0.3.3")
        update = self.updater.check()
        with self.assertRaises(core.UpdateCancelled):
            self.updater.install(update, progress=lambda p: p["stage"] != "extract")
        self.assertEqual(self.updater.status()["active"], "0.3.2")

    def test_hard_interruption_during_publication_allows_safe_retry(self):
        class SimulatedPowerCut(BaseException):
            """Bypasses normal Exception cleanup, as abrupt process death does."""
        self.install("0.3.2")
        for version, boundary in (("0.3.3", "complete-file"), ("0.3.4", "partial-file")):
            with self.subTest(boundary=boundary):
                previous = self.updater.status()["active"]
                update = self.release(version)
                if boundary == "complete-file":
                    real_publish = self.updater._publish
                    def interrupt(source, target, created):
                        real_publish(source, target, created)
                        if target.name == "talos_game_update.py":
                            raise SimulatedPowerCut()
                    patch = mock.patch.object(self.updater, "_publish", side_effect=interrupt)
                else:
                    real_copy = core.shutil.copyfileobj
                    interrupted = [False]
                    def interrupt_copy(source, target, length=0):
                        if not interrupted[0]:
                            interrupted[0] = True
                            target.write(source.read(2)); target.flush(); os.fsync(target.fileno())
                            raise SimulatedPowerCut()
                        return real_copy(source, target, length)
                    patch = mock.patch.object(core.shutil, "copyfileobj", side_effect=interrupt_copy)
                with patch:
                    try:
                        self.updater.install_local(update.feed_path, update.package_path)
                    except SimulatedPowerCut:
                        pass
                if boundary == "partial-file" and not interrupted[0]:
                    # An atomic stage-inode publication has no partial-copy
                    # boundary; a complete successful install is the safe result.
                    self.assertEqual(self.updater.status()["active"], version)
                    continue
                self.assertEqual(self.updater.status()["active"], previous)
                self.updater.install_local(update.feed_path, update.package_path)
                self.assertEqual(self.updater.status()["active"], version)

    def test_os_lock_is_released_after_abrupt_holder_exit(self):
        self.install()
        # The child imports only this reviewed updater and acquires a local lock.
        # No signing key or release entry point is given to it.
        code = """import importlib.util,os,sys
sys.dont_write_bytecode=True
s=importlib.util.spec_from_file_location('crash_lock_core',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
u=m.Updater(sys.argv[2],'unused-public-placeholder',node_path=sys.argv[3])
with u._lock(): os._exit(0)
"""
        result = subprocess.run([sys.executable, "-c", code, str(HERE / "talos_update_core.py"), str(self.root), self.node],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10, check=False)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        with self.updater._lock():
            pass
        self.assertEqual(self.updater.status()["active"], "0.3.2")

    def test_corrupted_rollback_target_is_never_activated(self):
        self.install("0.3.2")
        self.install("0.3.3")
        (self.root / "TalosHelper-App-0.3.2.exe").write_bytes(b"MZ-corrupted")
        before = json.loads((self.root / "talos-update-state.json").read_text())
        with self.assertRaises(core.ValidationError):
            self.updater.rollback("0.3.2")
        after = json.loads((self.root / "talos-update-state.json").read_text())
        self.assertEqual(after, before)

    def test_healthy_previous_version_can_recover_a_corrupt_active(self):
        self.install("0.3.2")
        self.install("0.3.3")
        (self.root / "TalosHelper-App-0.3.3.exe").write_bytes(b"MZ-corrupted-active")
        with self.assertRaises(core.ValidationError):
            self.updater.launch_active()
        self.assertEqual(self.updater.rollback("0.3.2")["active"], "0.3.2")
        self.assertEqual(self.updater.status()["active"], "0.3.2")


if __name__ == "__main__":
    unittest.main(verbosity=2)
