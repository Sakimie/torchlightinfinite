import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("online_game_helper_under_test", HERE / "game_helper.py")
helper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(helper)


class Variable:
    def __init__(self):
        self.text = ""

    def set(self, text):
        self.text = text


class App:
    def __init__(self, base, release):
        self._talos_base_root = base
        self._talos_install_root = release
        self.status = Variable()


class GameUpdateTests(unittest.TestCase):
    def test_existing_gameplay_thread_and_validation_guards_still_pass(self):
        source = HERE.parent / "legacy-update/test_talos_game_update.py"
        spec = importlib.util.spec_from_file_location("legacy_guards_for_online_helper", source)
        tests = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(tests)
        tests.bridge = helper
        suite = unittest.defaultTestLoader.loadTestsFromModule(tests)
        output = io.StringIO()
        with mock.patch.object(helper, "_add_update_button"):
            result = unittest.TextTestRunner(stream=output).run(suite)
        self.assertGreaterEqual(result.testsRun, 18)
        self.assertTrue(result.wasSuccessful(), output.getvalue())

    def test_update_click_only_launches_fixed_local_entry_and_keeps_release_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            release = base / "releases/0.3.2"
            release.mkdir(parents=True)
            launcher = base / "TalosHelper-启动器.exe"
            launcher.write_bytes(b"local launcher fixture")
            app = App(base, release)
            with mock.patch.object(helper.subprocess, "Popen") as process:
                helper._open_update_launcher(app)
            self.assertEqual(process.call_args.args[0], [str(launcher), "--updates"])
            self.assertFalse(process.call_args.kwargs["shell"])
            self.assertEqual(process.call_args.kwargs["cwd"], str(base))
            self.assertEqual(helper._root_dir(app), release)
            self.assertEqual(helper._base_dir(app), base)
            self.assertIn("仅点击其中", app.status.text)

    def test_missing_launcher_keeps_gameplay_usable_and_never_spawns(self):
        with tempfile.TemporaryDirectory() as folder:
            app = App(Path(folder), Path(folder) / "releases/0.3.2")
            with mock.patch.object(helper.subprocess, "Popen") as process:
                helper._open_update_launcher(app)
            process.assert_not_called()
            self.assertIn("首次更新器包", app.status.text)
            self.assertIn("离线使用", app.status.text)


if __name__ == "__main__":
    unittest.main()
