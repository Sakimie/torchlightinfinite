"""Focused tests for the small Python/Tk review and subprocess bridge."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("talos_game_update", HERE / "talos_game_update.py")
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)
METADATA = json.loads((HERE / "game-metadata.json").read_text(encoding="utf-8"))


def form():
    return {
        "day": "7", "position": 1, "phase": "manage", "totalDays": 20,
        "cells": [{"n": n, "kind": "office" if n == 0 else "locked", "event": False, "scratch": False, "ivory": False} for n in range(16)],
        "destinations": [], "tools": [], "vault": "0", "objective": "other", "objectiveProgress": "0",
        "usurpStage": "1", "usedAction": False, "chainArmed": False, "milestone": False,
        "chainConsumed": "", "eventKind": None, "offerIds": [], "resumePhase": None,
        "startEvent": "ordinary", "confirmed": True,
    }


class Variable:
    def __init__(self, value=""):
        self.value = value

    def set(self, value):
        self.value = value


class Output:
    def __init__(self):
        self.text = "old advice"

    def configure(self, **kwargs):
        pass

    def delete(self, *args):
        self.text = ""

    def insert(self, _, text):
        self.text += text


class Root:
    def __init__(self):
        self.callbacks = []
        self.mode = "normal"

    def after(self, delay, callback):
        self.callbacks.append((delay, callback))

    def winfo_exists(self):
        return True

    def winfo_children(self):
        return []

    def cget(self, name):
        return ""

    def state(self, value=None):
        if value:
            self.mode = value
        return self.mode

    def withdraw(self):
        self.mode = "withdrawn"


class App:
    def __init__(self):
        self.root, self.output = Root(), Output()
        self.status, self.review = Variable("recognized"), Variable()
        self.image, self.busy = object(), False
        self.old = {"board": {"2": {"species": "elephant", "tier": 0}}, "current": 2, "days": 13, "phase": "decision", "choices": []}

    def get_state(self):
        return copy.deepcopy(self.old)


class ReviewValidationTests(unittest.TestCase):
    def test_prefill_does_not_infer_current_day_or_missing_room_type(self):
        state = bridge.prefill_state({"days": 13, "current": 2, "board": {"2": {"species": "elephant", "tier": 0}}, "choices": [{"room": 4, "event": True}], "box": True})
        self.assertEqual(state["day"], "")
        self.assertEqual(state["remainingDaysHint"], 13)
        self.assertEqual(state["position"], 1)
        self.assertEqual(state["cells"][1]["beast"], "mammoth")
        self.assertEqual(state["cells"][2]["kind"], "unknown")
        self.assertEqual(state["cells"][3]["eventKind"], "unknown")
        self.assertEqual(state["tools"], ["revenge"])
        self.assertFalse(state["confirmed"])

    def test_previous_manual_fields_remain_unconfirmed(self):
        previous = form()
        state = bridge.prefill_state({}, previous)
        self.assertEqual(state["day"], "7")
        self.assertFalse(state["confirmed"])
        self.assertTrue(state["needsReview"])

    def test_valid_full_manual_form(self):
        state = form()
        state["cells"][1].update(kind="inmate", beast="elephant", tier="3")
        result = bridge.validate_board(state, METADATA)
        self.assertEqual(result["cells"][1]["beast"], "mammoth")
        self.assertEqual(result["day"], 7)
        self.assertEqual(result["usurpStage"], 1)
        self.assertTrue(result["confirmed"])
        self.assertIsNone(result["startEvent"])

    def test_missing_day_confirmation_usurp_or_room_blocks(self):
        for field, value in (("day", ""), ("confirmed", False), ("usurpStage", "unknown")):
            with self.subTest(field=field):
                state = form()
                state[field] = value
                with self.assertRaises(ValueError):
                    bridge.validate_board(state, METADATA)
        state = form()
        state["cells"][5]["kind"] = "unknown"
        with self.assertRaisesRegex(ValueError, "旧版空白"):
            bridge.validate_board(state, METADATA)

    def test_unsupported_information_is_not_ignored(self):
        for flag in ("preyPresent", "usedExtra", "devourAfterSwap", "hiddenKnownPresent", "refreshPresent"):
            with self.subTest(flag=flag):
                state = form()
                state[flag] = True
                with self.assertRaises(ValueError):
                    bridge.validate_board(state, METADATA)
        for mutation in (lambda state: state["cells"][3].update(scratch=True), lambda state: state.update(startEvent="special"), lambda state: state["cells"][1].update(kind="inmate", beast="lion", tier=5)):
            state = form()
            mutation(state)
            with self.assertRaises(ValueError):
                bridge.validate_board(state, METADATA)

    def test_chain_requires_actual_consumed_count(self):
        state = form()
        state.update(phase="chain", chainConsumed="0")
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)
        state["chainConsumed"] = "2"
        self.assertEqual(bridge.validate_board(state, METADATA)["chainConsumed"], 2)

    def test_unlisted_tool_or_event_cannot_be_omitted_after_confirmation(self):
        for flag, expected in (("unsupportedToolPresent", "捕兽夹"), ("unsupportedEventPresent", "同罪移押")):
            with self.subTest(flag=flag):
                state = form()
                state[flag] = True
                with self.assertRaisesRegex(ValueError, expected):
                    bridge.validate_board(state, METADATA)

    def test_nest_due_and_unsupported_revealed_event_require_manual_information(self):
        state = form()
        state["cells"][3].update(kind="nest", due="7")
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)
        state["cells"][3]["due"] = "9"
        self.assertEqual(bridge.validate_board(state, METADATA)["cells"][3]["due"], 9)
        state["cells"][3].update(event=True, eventKind="unsupported-event")
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)

    def test_unrevealed_event_door_stays_unknown_without_inventing_type(self):
        state = form()
        state.update(phase="patrol", destinations=[3])
        state["cells"][3].update(event=True, eventKind="unknown")
        board = bridge.validate_board(state, METADATA)
        self.assertTrue(board["cells"][3]["event"])
        self.assertNotIn("eventKind", board["cells"][3])
        state.update(phase="event", eventKind=None)
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)

    def test_reward_candidates_and_resume_are_explicit(self):
        state = form()
        state.update(phase="gift", resumePhase="manage")
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)
        state["offerIds"] = [METADATA["tools"][0]["id"]]
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)
        state["offerIds"] = [METADATA["gifts"][0]["id"]]
        self.assertEqual(bridge.validate_board(state, METADATA)["resumePhase"], "manage")
        state.update(resumePhase="chain", chainConsumed="")
        with self.assertRaises(ValueError):
            bridge.validate_board(state, METADATA)


class BridgeLifetimeTests(unittest.TestCase):
    def test_recognition_worker_never_touches_tk_and_main_thread_delivers(self):
        app = App()
        main_thread = threading.get_ident()
        seen = []

        def old_analyze(image):
            raise AssertionError("unsafe old analyze must not execute")

        def recognize(image):
            seen.append(("recognize", threading.get_ident(), image))
            return {"board": {}, "phase": "decision"}

        app.analyze = old_analyze
        app.draw_preview = lambda: self.assertEqual(threading.get_ident(), main_thread)

        def finish(result, error, seconds):
            self.assertEqual(threading.get_ident(), main_thread)
            app.busy = False
            seen.append(("finish", result, error))

        app.finish_analysis = finish
        original_after = app.root.after

        def guarded_after(delay, callback):
            self.assertEqual(threading.get_ident(), main_thread, "worker must not call root.after")
            original_after(delay, callback)

        app.root.after = guarded_after
        with mock.patch.dict(old_analyze.__globals__, {"recognize": recognize}):
            bridge.calculate(app, keep_status=True)
            image = object()
            app.analyze(image)
            self.assertTrue(app.busy)
            message = app._talos_game_update["queue"].get(timeout=2)
            self.assertEqual(message["kind"], "recognition")
            self.assertNotEqual(seen[0][1], main_thread)
            self.assertEqual(len(seen), 1, "worker has not applied any Tk result")
            app._talos_game_update["queue"].put(message)
            app.root.callbacks.pop(0)[1]()
        self.assertFalse(app.busy)
        self.assertEqual(seen[1], ("finish", {"board": {}, "phase": "decision"}, None))

    def test_recognition_failure_returns_busy_and_old_generation_cannot_apply(self):
        app = App()
        applied = []

        def old_analyze(image):
            raise AssertionError("unsafe old analyze must not execute")

        def fail(image):
            raise RuntimeError("synthetic recognition failure")

        def finish(result, error, seconds):
            app.busy = False
            app.status.set("识别失败：" + error if error else "识别完成")
            applied.append(result)

        app.analyze, app.draw_preview, app.finish_analysis = old_analyze, lambda: None, finish
        with mock.patch.dict(old_analyze.__globals__, {"recognize": fail}):
            bridge.calculate(app, keep_status=True)
            app.analyze(object())
            state = app._talos_game_update
            message = state["queue"].get(timeout=2)
            state["queue"].put(message)
            app.root.callbacks.pop(0)[1]()
        self.assertFalse(app.busy)
        self.assertIn("synthetic recognition failure", app.status.value)
        self.assertNotIn("old advice", app.output.text)
        self.assertEqual(applied, [None])
        old_token = message["recognitionGeneration"]
        state["recognition_generation"] = old_token + 1
        state["recognition_active"] = old_token + 1
        app.image, app.busy = object(), True
        bridge._deliver_recognition(app, message)
        self.assertTrue(app.busy, "old result must not release a newer recognition's busy flag")
        self.assertEqual(applied, [None])

    def test_screenshot_callback_clears_advice_without_dialog_or_node(self):
        app = App()
        with mock.patch.object(bridge, "ReviewDialog") as dialog, mock.patch.object(bridge, "_invoke_node") as invoke:
            bridge.calculate(app, keep_status=True)
        dialog.assert_not_called()
        invoke.assert_not_called()
        self.assertIn("旧推荐已清除", app.output.text)
        self.assertEqual(app.status.value, "recognized")

    def test_loading_example_never_opens_dialog(self):
        app = App()
        app.image = None

        def load_example():
            bridge.calculate(app)

        with mock.patch.object(bridge, "ReviewDialog") as dialog:
            load_example()
        dialog.assert_not_called()
        self.assertIn("示例研究", app.output.text)

    def test_monitor_invalidates_new_image_and_stale_node_return(self):
        app = App()
        bridge.calculate(app, keep_status=True)
        state = app._talos_game_update
        old_signature, generation = bridge._signature(app), state["generation"]
        app.image = object()
        app.root.callbacks.pop(0)[1]()
        self.assertGreater(state["generation"], generation)
        bridge._deliver(app, {"generation": generation, "signature": old_signature, "result": {"ok": True, "primary": {"label": "STALE"}}})
        self.assertNotIn("STALE", app.output.text)

    def test_capture_temporarily_hides_and_restores_host_window(self):
        app = App()
        seen = []
        app.capture = lambda: seen.append(app.root.state())
        bridge.calculate(app, keep_status=True)
        app.capture()
        self.assertEqual(app.root.state(), "withdrawn")
        callback = next(callback for delay, callback in app.root.callbacks if delay == 160)
        callback()
        self.assertEqual(seen, ["withdrawn"])
        self.assertEqual(app.root.state(), "normal")

    def test_actual_selected_strategy_is_visible_in_output_and_overlay(self):
        for validation, expected in (({"ready": True, "selected": "baseline"}, "修正规则基线（历史参数复验未胜出）"), ({"ready": True, "selected": "historical"}, "历史参数（经本轮复验）"), ({"ready": False}, "历史参数试用，尚未复验")):
            with self.subTest(validation=validation):
                app = App()
                bridge.calculate(app, keep_status=True)
                state = app._talos_game_update
                state["overlay_text"] = Variable()
                bridge._deliver(app, {"generation": state["generation"], "signature": bridge._signature(app), "result": {"ok": True, "primary": {"label": "结束本日"}, "validation": validation}})
                self.assertIn("当前策略：" + expected, app.output.text)
                self.assertIn("当前策略：" + expected, state["overlay_text"].value)

    def test_node_transport_uses_stdin_timeout_and_no_shell(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "training").mkdir()
            (root / "training/game-advice.cjs").write_text("// stub", encoding="utf-8")
            completed = subprocess.CompletedProcess([], 0, '{"ok":true,"primary":{"label":"测试"}}', "")
            with mock.patch.object(bridge.subprocess, "run", return_value=completed) as run:
                result = bridge._invoke_node("portable/node.exe", root, form())
            self.assertEqual(result["primary"]["label"], "测试")
            kwargs = run.call_args.kwargs
            self.assertFalse(kwargs["shell"])
            self.assertEqual(kwargs["timeout"], 20)
            self.assertEqual(kwargs["cwd"], str(root))
            self.assertIn("board", json.loads(kwargs["input"]))


if __name__ == "__main__":
    unittest.main()
