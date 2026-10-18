import importlib.util
from pathlib import Path
import queue
import threading
import unittest


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("launcher_ui_under_test", HERE / "launcher_ui.py")
ui = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ui)


class Cancelled(Exception):
    code = "cancelled"


class Core:
    def __init__(self, root, key, feed_url=""):
        self.calls = [("construct", threading.get_ident())]
        self.active = "0.3.2"
        self.feed_url = feed_url
        self.check_gate = None
        self.download_gate = None
        self.entered = threading.Event()
        self.fail_check = False
        self.latest = {"version": "0.3.3", "notes": ["修复截图待核对提示", "游戏建议继续离线"], "package": {"bytes": 5 * 1024 * 1024}}

    def record(self, name):
        self.calls.append((name, threading.get_ident()))

    def status(self):
        self.record("status")
        return {"active": self.active, "history": ["0.3.2", "0.3.3"] if self.active == "0.3.3" else ["0.3.2"], "installed": {}}

    def check(self):
        self.record("check")
        self.entered.set()
        if self.check_gate:
            self.check_gate.wait(3)
        if self.fail_check:
            raise RuntimeError("network unavailable")
        return self.latest

    def install(self, manifest, progress=None):
        self.record("download")
        assert manifest is self.latest, "UI must preserve authenticated manifest identity"
        progress({"stage": "download", "done": 0, "total": 10, "message": "开始下载"})
        self.entered.set()
        if self.download_gate:
            self.download_gate.wait(3)
        if not progress({"stage": "download", "done": 5, "total": 10, "message": "下载中"}):
            raise Cancelled("cancelled")
        self.active = manifest["version"]
        return {"version": self.active}

    def install_local(self, feed, archive):
        self.record("install_local")
        self.local_paths = (feed, archive)
        self.active = "0.3.3"
        return {"version": self.active}

    def rollback(self):
        self.record("rollback")
        self.active = "0.3.2"
        return {"version": self.active}

    def launch_active(self):
        self.record("launch")
        return {"version": self.active, "pid": 123}


def terminal(controller):
    while True:
        event = controller.events.get(timeout=4)
        if event["type"] != "progress":
            return event


class Var:
    def __init__(self):
        self.value = ""
        self.owner = threading.get_ident()

    def set(self, value):
        assert threading.get_ident() == self.owner, "Tk state must only change on UI thread"
        self.value = value


class Widget:
    def __init__(self):
        self.options = {}
        self.owner = threading.get_ident()
        self.text = ""

    def configure(self, **kwargs):
        assert threading.get_ident() == self.owner
        self.options.update(kwargs)

    def start(self, *args):
        assert threading.get_ident() == self.owner

    stop = start

    def delete(self, *args):
        assert threading.get_ident() == self.owner
        self.text = ""

    def insert(self, _, text):
        assert threading.get_ident() == self.owner
        self.text += text


def headless_view(controller):
    view = object.__new__(ui.Launcher)
    view.controller = controller
    view.local_status = {"active": "0.3.2", "history": ["0.3.2"], "installed": {}}
    view.manifest = None
    view.display_token = controller.token
    for name in ("version_text", "status_text", "release_text", "size_text", "history_text"):
        setattr(view, name, Var())
    for name in ("launch_button", "check_button", "refresh_button", "install_button", "local_button", "rollback_button", "cancel_button", "notes", "progress"):
        setattr(view, name, Widget())
    view.buttons = [view.launch_button, view.check_button, view.refresh_button, view.install_button, view.local_button, view.rollback_button]
    return view


class ControllerTests(unittest.TestCase):
    def controller(self, feed="https://updates.example.test/latest.json"):
        return ui.LauncherController("/tmp/talos-test-root", "TEST PUBLIC KEY", feed, updater_factory=Core)

    def test_startup_status_and_game_launch_never_check_or_download(self):
        controller = self.controller()
        self.assertTrue(controller.start("status"))
        event = terminal(controller)
        self.assertEqual(event["status"]["active"], "0.3.2")
        self.assertFalse(controller.start("launch"), "a terminal event must be applied before another operation")
        controller.finish(event["token"])
        self.assertTrue(controller.start("launch"))
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual([name for name, _ in controller.updater.calls], ["construct", "status", "launch"])
        self.assertTrue(all(owner != threading.get_ident() for _, owner in controller.updater.calls))

    def test_explicit_check_and_install_keep_authenticated_manifest(self):
        controller = self.controller()
        controller.start("check")
        event = terminal(controller)
        self.assertIs(event["manifest"], controller.updater.latest)
        controller.finish(event["token"])
        controller.start("install", event["manifest"])
        installed = terminal(controller)
        controller.finish(installed["token"])
        self.assertEqual(installed["status"]["active"], "0.3.3")

    def test_cancel_download_keeps_old_active(self):
        controller = self.controller()
        controller.start("status")
        controller.finish(terminal(controller)["token"])
        controller.updater.download_gate = threading.Event()
        controller.start("install", controller.updater.latest)
        self.assertTrue(controller.updater.entered.wait(2))
        self.assertTrue(controller.cancel())
        controller.updater.download_gate.set()
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["type"], "cancelled")
        self.assertEqual(controller.updater.active, "0.3.2")

    def test_cancel_check_discards_late_network_result(self):
        controller = self.controller()
        controller.start("status")
        controller.finish(terminal(controller)["token"])
        controller.updater.check_gate = threading.Event()
        controller.start("check")
        self.assertTrue(controller.updater.entered.wait(2))
        controller.cancel()
        controller.updater.check_gate.set()
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["type"], "cancelled")
        self.assertNotIn("manifest", event)

    def test_missing_feed_avoids_network_and_local_import_rollback_stay_offline(self):
        controller = self.controller(feed="")
        controller.start("check")
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["type"], "error")
        self.assertIn("尚未配置", event["message"])
        controller.start("install_local", ("signed-feed.json", "package.zip"))
        controller.finish(terminal(controller)["token"])
        controller.start("rollback")
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["status"]["active"], "0.3.2")
        self.assertEqual(controller.updater.local_paths, ("signed-feed.json", "package.zip"))
        self.assertNotIn("check", [name for name, _ in controller.updater.calls])
        self.assertNotIn("download", [name for name, _ in controller.updater.calls])

    def test_network_error_can_still_launch_existing_version(self):
        controller = self.controller()
        controller.start("status")
        controller.finish(terminal(controller)["token"])
        controller.updater.fail_check = True
        controller.start("check")
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["type"], "error")
        controller.start("launch")
        event = terminal(controller)
        controller.finish(event["token"])
        self.assertEqual(event["launched"]["version"], "0.3.2")

    def test_ui_displays_notes_size_and_error_on_main_thread(self):
        controller = self.controller()
        controller.start("check")
        event = terminal(controller)
        view = headless_view(controller)
        view._event(event)
        self.assertIn("0.3.3", view.release_text.value)
        self.assertEqual(view.size_text.value, "下载大小：5.00 MB")
        self.assertIn("游戏建议继续离线", view.notes.text)
        self.assertEqual(view.install_button.options["state"], "normal")
        self.assertNotIn("download", [name for name, _ in controller.updater.calls], "showing notes must not download")
        controller.start("check")
        controller.updater.fail_check = True
        # A directly delivered error tests presentation independently of request timing.
        view.display_token = controller.token
        view._event({"token": controller.token, "operation": "check", "type": "error", "message": "网络中断"})
        self.assertIn("已安装版本仍保留", view.status_text.value)
        self.assertEqual(view.launch_button.options["state"], "normal")
        controller.worker.join(3)

    def test_failed_status_keeps_recovery_and_local_import_enabled(self):
        controller = self.controller()
        controller.start("status")
        controller.worker.join(3)
        view = headless_view(controller)
        view.local_status = {"active": None, "history": [], "installed": {}}
        view._event({"token": controller.token, "operation": "status", "type": "error", "message": "active state unavailable"})
        self.assertEqual(view.rollback_button.options["state"], "normal", "core must be allowed to select a healthy old version without a successful status")
        self.assertEqual(view.local_button.options["state"], "normal")
        self.assertEqual(view.launch_button.options["state"], "disabled")
        self.assertIn("回退健康旧版", view.status_text.value)

    def test_damaged_active_disables_launch_but_allows_healthy_rollback_or_signed_repair(self):
        controller = self.controller()
        controller.start("status")
        controller.worker.join(3)
        controller.finish(controller.token)
        view = headless_view(controller)
        view._status({"active": "0.3.3", "active_valid": False, "history": ["0.3.1", "0.3.2", "0.3.3"], "installed": {"0.3.2": {"version": "0.3.2"}}, "unavailable": [{"version": "0.3.3", "error": "missing file"}]})
        view.manifest = controller.updater.latest
        view._controls()
        self.assertEqual(view.launch_button.options["state"], "disabled")
        self.assertEqual(view.rollback_button.options["state"], "normal")
        self.assertEqual(view.install_button.options["state"], "normal", "a signed matching package can repair the damaged active version")
        self.assertEqual(view.local_button.options["state"], "normal")
        self.assertIn("文件异常", view.version_text.value)
        self.assertNotIn("0.3.1", view.history_text.value, "unhealthy history entries are not presented as rollback targets")


if __name__ == "__main__":
    unittest.main()
